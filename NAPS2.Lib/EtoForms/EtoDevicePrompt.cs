﻿using NAPS2.EtoForms.Ui;
using NAPS2.Scan;

namespace NAPS2.EtoForms;

public class EtoDevicePrompt : IDevicePrompt
{
    private readonly IFormFactory _formFactory;
    private readonly ScanningContext _scanningContext;

    public EtoDevicePrompt(IFormFactory formFactory, ScanningContext scanningContext)
    {
        _formFactory = formFactory;
        _scanningContext = scanningContext;
    }

    public Task<DeviceChoice> PromptForDevice(ScanOptions options, bool allowAlwaysAsk)
    {
        // TODO: Extension method or something to turn InvokeGet into Task<T>?
        return Task.FromResult(Invoker.Current.InvokeGet(() =>
        {
            var deviceForm = _formFactory.Create<SelectDeviceForm>();
            deviceForm.ScanOptions = options;
            deviceForm.AllowAlwaysAsk = allowAlwaysAsk;
            deviceForm.ShowModal();
            return deviceForm.Choice;
        }));
    }
}