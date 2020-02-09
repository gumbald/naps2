﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;
using NAPS2.Lang.Resources;
using NAPS2.Logging;
using NAPS2.Operation;
using NAPS2.Images.Storage;
using NAPS2.Util;
using NAPS2.WinForms;

namespace NAPS2.Update
{
    public class UpdateOperation : OperationBase
    {
        private readonly ImageContext imageContext;
        private readonly ErrorOutput errorOutput;

        private readonly ManualResetEvent waitHandle = new ManualResetEvent(false);
        private WebClient? client;
        private UpdateInfo? update;
        private string? tempFolder;
        private string? tempPath;

        static UpdateOperation()
        {
            try
            {
                const int tls13 = 12288;
                ServicePointManager.SecurityProtocol = (SecurityProtocolType) tls13;
            }
            catch (NotSupportedException)
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
        }

        public UpdateOperation(ImageContext imageContext, ErrorOutput errorOutput)
        {
            this.imageContext = imageContext;
            this.errorOutput = errorOutput;

            ProgressTitle = MiscResources.UpdateProgress;
            AllowBackground = true;
            AllowCancel = true;
            Status = new OperationStatus
            {
                StatusText = MiscResources.Updating,
                ProgressType = OperationProgressType.MB
            };
        }

        public void Start(UpdateInfo updateInfo)
        {
            update = updateInfo;
            tempFolder = Path.Combine(Paths.Temp, Path.GetRandomFileName());
            Directory.CreateDirectory(tempFolder);
            tempPath = Path.Combine(tempFolder, updateInfo.DownloadUrl.Substring(updateInfo.DownloadUrl.LastIndexOf('/') + 1));

            client = new WebClient();
            client.DownloadProgressChanged += DownloadProgress;
            client.DownloadFileCompleted += DownloadCompleted;
            client.DownloadFileAsync(new Uri(updateInfo.DownloadUrl), tempPath);
        }

        public override void Cancel()
        {
            client.CancelAsync();
        }

        public override void Wait(CancellationToken cancelToken)
        {
            while (!waitHandle.WaitOne(1000) && !cancelToken.IsCancellationRequested)
            {
            }
        }

        private void DownloadCompleted(object sender, AsyncCompletedEventArgs e)
        {
            try
            {
                if (e.Cancelled)
                {
                    return;
                }
                if (e.Error != null)
                {
                    e.Error.PreserveStackTrace();
                    throw e.Error;
                }
                if (!VerifyHash())
                {
                    Log.Error($"Update error for {update.Name}: hash does not match");
                    errorOutput.DisplayError(MiscResources.UpdateError);
                    return;
                }
                if (!VerifySignature())
                {
                    Log.Error($"Update error for {update.Name}: signature does not validate");
                    errorOutput.DisplayError(MiscResources.UpdateError);
                    return;
                }

#if STANDALONE
                InstallZip();
#else
                InstallExe();
#endif
            }
            catch (Exception ex)
            {
                Log.ErrorException("Update error", ex);
                errorOutput.DisplayError(MiscResources.UpdateError);
                return;
            }
            finally
            {
                InvokeFinished();
                waitHandle.Set();
            }
            // TODO: Simplify
            var desktop = Application.OpenForms.OfType<FDesktop>().FirstOrDefault();
            if (desktop != null)
            {
                desktop.SkipRecoveryCleanup = true;
                desktop.Close();
            }
        }

        private void InstallExe()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/SILENT /CLOSEAPPLICATIONS"
            });
        }

        private void InstallZip()
        {
            ZipFile.ExtractToDirectory(tempPath, tempFolder);
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string portableLauncherPath = Path.Combine(assemblyFolder, "..", "..", "NAPS2.Portable.exe");
            AtomicReplaceFile(Path.Combine(tempFolder, "NAPS2.Portable.exe"), portableLauncherPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = portableLauncherPath,
                Arguments = $"/Update {Process.GetCurrentProcess().Id} \"{Path.Combine(tempFolder, "App")}\""
            });
        }

        private void AtomicReplaceFile(string source, string dest)
        {
            if (!File.Exists(dest))
            {
                File.Move(source, dest);
                return;
            }
            string temp = dest + ".old";
            File.Move(dest, temp);
            try
            {
                File.Move(source, dest);
                File.Delete(temp);
            }
            catch (Exception)
            {
                File.Move(temp, dest);
                throw;
            }
        }

        private bool VerifyHash()
        {
            using var sha = new SHA1CryptoServiceProvider();
            using FileStream stream = File.OpenRead(tempPath);
            byte[] checksum = sha.ComputeHash(stream);
            return checksum.SequenceEqual(update.Sha1);
        }

        private bool VerifySignature()
        {
            var cert = new X509Certificate2(ClientCreds.naps2_public);
            var csp = (RSACryptoServiceProvider)cert.PublicKey.Key;
            return csp.VerifyHash(update.Sha1, CryptoConfig.MapNameToOID("SHA1"), update.Signature);
        }

        private void DownloadProgress(object sender, DownloadProgressChangedEventArgs e)
        {
            Status.CurrentProgress = (int)e.BytesReceived;
            Status.MaxProgress = (int)e.TotalBytesToReceive;
            InvokeStatusChanged();
        }
    }
}
