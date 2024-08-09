using System.Globalization;
using System.Threading;
using Eto.Drawing;
using Eto.Forms;
using NAPS2.EtoForms.Layout;
using NAPS2.EtoForms.Widgets;
using NAPS2.Scan;
using NAPS2.Scan.Internal;

namespace NAPS2.EtoForms.Ui;

public class EditProfileForm : EtoDialogBase
{
    private readonly ErrorOutput _errorOutput;
    private readonly ProfileNameTracker _profileNameTracker;
    private readonly DeviceCapsCache _deviceCapsCache;

    private readonly TextBox _displayName = new();
    private readonly DeviceSelectorWidget _deviceSelectorWidget;
    private readonly RadioButton _predefinedSettings;
    private readonly RadioButton _nativeUi;
    private readonly LayoutVisibility _nativeUiVis = new(true);
    private readonly EnumDropDownWidget<ScanSource> _paperSource = new();
    private readonly DropDown _pageSize = C.EnumDropDown<ScanPageSize>();
    private readonly DropDownWidget<int> _resolution = new();
    private readonly EnumDropDownWidget<ScanBitDepth> _bitDepth = new();
    private readonly EnumDropDownWidget<ScanHorizontalAlign> _horAlign = new();
    private readonly EnumDropDownWidget<ScanScale> _scale = new();
    private readonly CheckBox _enableAutoSave = new() { Text = UiStrings.EnableAutoSave };
    private readonly LinkButton _autoSaveSettings = new() { Text = UiStrings.AutoSaveSettings };
    private readonly Button _advanced = new() { Text = UiStrings.Advanced };
    private readonly SliderWithTextBox _brightnessSlider = new();
    private readonly SliderWithTextBox _contrastSlider = new();

    private ScanProfile _scanProfile = null!;
    private bool _isDefault;
    private bool _result;
    private bool _suppressChangeEvent;
    private bool _suppressPageSizeEvent;
    private CancellationTokenSource? _updateCapsCts;

    public EditProfileForm(Naps2Config config, IScanPerformer scanPerformer, ErrorOutput errorOutput,
        ProfileNameTracker profileNameTracker, DeviceCapsCache deviceCapsCache) : base(config)
    {
        _errorOutput = errorOutput;
        _profileNameTracker = profileNameTracker;
        _deviceCapsCache = deviceCapsCache;
        _deviceSelectorWidget = new(scanPerformer, deviceCapsCache, this)
        {
            ProfileFunc = GetUpdatedScanProfile,
            AllowAlwaysAsk = true
        };
        _deviceSelectorWidget.DeviceChanged += DeviceChanged;

        _predefinedSettings = new RadioButton { Text = UiStrings.UsePredefinedSettings };
        _nativeUi = new RadioButton(_predefinedSettings) { Text = UiStrings.UseNativeUi };
        _resolution.Format = x => string.Format(SettingsResources.DpiFormat, x.ToString(CultureInfo.InvariantCulture));
        _paperSource.SelectedItemChanged += PaperSource_SelectedItemChanged;
        _pageSize.SelectedIndexChanged += PageSize_SelectedIndexChanged;
        _predefinedSettings.CheckedChanged += PredefinedSettings_CheckedChanged;
        _nativeUi.CheckedChanged += NativeUi_CheckedChanged;

        _enableAutoSave.CheckedChanged += EnableAutoSave_CheckedChanged;
        _autoSaveSettings.Click += AutoSaveSettings_LinkClicked;
        _advanced.Click += Advanced_Click;
    }

    public void SetDevice(ScanDevice device)
    {
        _deviceSelectorWidget.Choice = DeviceChoice.ForDevice(device);
    }

    private void DeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        if (e.NewChoice.Device != null && (string.IsNullOrEmpty(_displayName.Text) ||
                                           e.PreviousChoice.Device?.Name == _displayName.Text))
        {
            _displayName.Text = e.NewChoice.Device.Name;
        }
        DeviceDriver = e.NewChoice.Driver;
        IconUri = e.NewChoice.Device?.IconUri;

        UpdateCaps();
        UpdateEnabledControls();
    }

    protected override void BuildLayout()
    {
        Title = UiStrings.EditProfileFormTitle;
        Icon = new Icon(1f, Icons.blueprints_small.ToEtoImage());

        FormStateController.DefaultExtraLayoutSize = new Size(60, 0);
        FormStateController.FixedHeightLayout = true;

        LayoutController.Content = L.Column(
            C.Label(UiStrings.DisplayNameLabel),
            _displayName,
            C.Spacer(),
            _deviceSelectorWidget,
            C.Spacer(),
            PlatformCompat.System.IsWiaDriverSupported || PlatformCompat.System.IsTwainDriverSupported
                ? L.Row(
                    _predefinedSettings,
                    _nativeUi
                ).Visible(_nativeUiVis)
                : C.None(),
            C.Spacer(),
            L.Row(
                L.Column(
                    C.Label(UiStrings.PaperSourceLabel),
                    _paperSource,
                    C.Label(UiStrings.PageSizeLabel),
                    _pageSize,
                    C.Label(UiStrings.ResolutionLabel),
                    _resolution,
                    C.Label(UiStrings.BrightnessLabel),
                    _brightnessSlider
                ).Scale(),
                L.Column(
                    C.Label(UiStrings.BitDepthLabel),
                    _bitDepth,
                    C.Label(UiStrings.HorizontalAlignLabel),
                    _horAlign,
                    C.Label(UiStrings.ScaleLabel),
                    _scale,
                    C.Label(UiStrings.ContrastLabel),
                    _contrastSlider
                ).Scale()
            ),
            L.Row(
                _enableAutoSave,
                _autoSaveSettings
            ),
            C.Filler(),
            L.Row(
                _advanced,
                C.Filler(),
                L.OkCancel(
                    C.OkButton(this, SaveSettings),
                    C.CancelButton(this))
            )
        );
    }

    public bool Result => _result;

    public ScanProfile ScanProfile
    {
        get => _scanProfile;
        set => _scanProfile = value.Clone();
    }

    public bool NewProfile { get; set; }

    private void UpdateUiForCaps()
    {
        _suppressChangeEvent = true;

        _paperSource.Items = ScanProfile.Caps?.PaperSources is [_, ..] paperSources
            ? paperSources
            : EnumDropDownWidget<ScanSource>.DefaultItems;

        var selectedSource = _paperSource.SelectedItem;

        var validResolutions = selectedSource switch
        {
            ScanSource.Glass => ScanProfile.Caps?.GlassResolutions,
            ScanSource.Feeder => ScanProfile.Caps?.FeederResolutions,
            ScanSource.Duplex => ScanProfile.Caps?.DuplexResolutions,
            _ => null
        };
        _resolution.Items = validResolutions is [_, ..]
            ? validResolutions
            : EnumDropDownWidget<ScanDpi>.DefaultItems.Select(x => x.ToIntDpi());

        _suppressChangeEvent = false;
    }

    private void UpdateCaps()
    {
        var cts = new CancellationTokenSource();
        _updateCapsCts?.Cancel();
        _updateCapsCts = cts;
        var updatedProfile = GetUpdatedScanProfile();
        var cachedCaps = _deviceCapsCache.GetCachedCaps(updatedProfile);
        if (cachedCaps != null)
        {
            ScanProfile.Caps = MapCaps(cachedCaps);
        }
        else
        {
            ScanProfile.Caps = null;
            if (updatedProfile.Device != null)
            {
                Task.Run(async () =>
                {
                    var caps = await _deviceCapsCache.QueryCaps(updatedProfile);
                    if (caps != null)
                    {
                        Invoker.Current.Invoke(() =>
                        {
                            if (!cts.IsCancellationRequested)
                            {
                                ScanProfile.Caps = MapCaps(caps);
                                UpdateUiForCaps();
                            }
                        });
                    }
                });
            }
        }
        UpdateUiForCaps();
    }

    private ScanProfileCaps MapCaps(ScanCaps? caps)
    {
        List<ScanSource>? paperSources = null;
        if (caps?.PaperSourceCaps is { } paperSourceCaps)
        {
            paperSources = new List<ScanSource>();
            if (paperSourceCaps.SupportsFlatbed) paperSources.Add(ScanSource.Glass);
            if (paperSourceCaps.SupportsFeeder) paperSources.Add(ScanSource.Feeder);
            if (paperSourceCaps.SupportsDuplex) paperSources.Add(ScanSource.Duplex);
        }

        return new ScanProfileCaps
        {
            PaperSources = paperSources,
            FeederCheck = caps?.PaperSourceCaps?.CanCheckIfFeederHasPaper,
            GlassResolutions = caps?.FlatbedCaps?.DpiCaps?.CommonValues?.ToList(),
            FeederResolutions = caps?.FeederCaps?.DpiCaps?.CommonValues?.ToList(),
            DuplexResolutions = caps?.DuplexCaps?.DpiCaps?.CommonValues?.ToList()
        };
    }

    private Driver DeviceDriver { get; set; }

    private string? IconUri { get; set; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Don't trigger any onChange events
        _suppressChangeEvent = true;

        DeviceDriver = new ScanOptionsValidator().ValidateDriver(
            Enum.TryParse<Driver>(ScanProfile.DriverName, true, out var driver)
                ? driver
                : Driver.Default);
        IconUri = ScanProfile.Device?.IconUri;

        _displayName.Text = ScanProfile.DisplayName;
        if (_deviceSelectorWidget.Choice == DeviceChoice.None)
        {
            var device = ScanProfile.Device?.ToScanDevice(DeviceDriver);
            if (device != null)
            {
                _deviceSelectorWidget.Choice = DeviceChoice.ForDevice(device);
            }
            else if (!NewProfile)
            {
                _deviceSelectorWidget.Choice = DeviceChoice.ForAlwaysAsk(DeviceDriver);
            }
        }
        _isDefault = ScanProfile.IsDefault;

        _paperSource.SelectedItem = ScanProfile.PaperSource;
        _bitDepth.SelectedItem = ScanProfile.BitDepth;
        _resolution.SelectedItem = ScanProfile.Resolution.Dpi;
        _contrastSlider.IntValue = ScanProfile.Contrast;
        _brightnessSlider.IntValue = ScanProfile.Brightness;
        UpdatePageSizeList();
        SelectPageSize();
        _scale.SelectedItem = ScanProfile.AfterScanScale;
        _horAlign.SelectedItem = ScanProfile.PageAlign;

        _enableAutoSave.Checked = ScanProfile.EnableAutoSave;

        _nativeUi.Checked = ScanProfile.UseNativeUI;
        _predefinedSettings.Checked = !ScanProfile.UseNativeUI;

        // Start triggering onChange events again
        _suppressChangeEvent = false;

        UpdateUiForCaps();
        UpdateEnabledControls();
    }

    private void UpdatePageSizeList()
    {
        _suppressPageSizeEvent = true;
        _pageSize.Items.Clear();

        // Defaults
        foreach (ScanPageSize item in Enum.GetValues(typeof(ScanPageSize)))
        {
            _pageSize.Items.Add(new PageSizeListItem
            {
                Type = item,
                Text = item.Description()
            });
        }

        // Custom Presets
        foreach (var preset in Config.Get(c => c.CustomPageSizePresets).OrderBy(x => x.Name))
        {
            _pageSize.Items.Insert(_pageSize.Items.Count - 1, new PageSizeListItem
            {
                Type = ScanPageSize.Custom,
                Text = string.Format(MiscResources.NamedPageSizeFormat, preset.Name, preset.Dimens.Width,
                    preset.Dimens.Height, preset.Dimens.Unit.Description()),
                CustomName = preset.Name,
                CustomDimens = preset.Dimens
            });
        }
        _suppressPageSizeEvent = false;
    }

    private void SelectPageSize()
    {
        if (ScanProfile.PageSize == ScanPageSize.Custom)
        {
            if (ScanProfile.CustomPageSize != null)
            {
                SelectCustomPageSize(ScanProfile.CustomPageSizeName, ScanProfile.CustomPageSize);
            }
            else
            {
                _pageSize.SelectedIndex = 0;
            }
        }
        else
        {
            _pageSize.SelectedIndex = (int) ScanProfile.PageSize;
        }
    }

    private void SelectCustomPageSize(string? name, PageDimensions dimens)
    {
        for (int i = 0; i < _pageSize.Items.Count; i++)
        {
            var item = (PageSizeListItem) _pageSize.Items[i];
            if (item.Type == ScanPageSize.Custom && item.CustomName == name && item.CustomDimens == dimens)
            {
                _pageSize.SelectedIndex = i;
                return;
            }
        }

        // Not found, so insert a new item
        _pageSize.Items.Insert(_pageSize.Items.Count - 1, new PageSizeListItem
        {
            Type = ScanPageSize.Custom,
            Text = string.IsNullOrEmpty(name)
                ? string.Format(MiscResources.CustomPageSizeFormat, dimens.Width, dimens.Height,
                    dimens.Unit.Description())
                : string.Format(MiscResources.NamedPageSizeFormat, name, dimens.Width, dimens.Height,
                    dimens.Unit.Description()),
            CustomName = name,
            CustomDimens = dimens
        });
        _pageSize.SelectedIndex = _pageSize.Items.Count - 2;
    }

    private bool SaveSettings()
    {
        if (_displayName.Text == "")
        {
            _errorOutput.DisplayError(MiscResources.NameMissing);
            return false;
        }
        if (_deviceSelectorWidget.Choice == DeviceChoice.None)
        {
            _errorOutput.DisplayError(MiscResources.NoDeviceSelected);
            return false;
        }
        _result = true;

        if (ScanProfile.IsLocked)
        {
            if (!ScanProfile.IsDeviceLocked)
            {
                ScanProfile.Device = ScanProfileDevice.FromScanDevice(_deviceSelectorWidget.Choice.Device);
            }
            return true;
        }
        if (ScanProfile.DisplayName != null)
        {
            _profileNameTracker.RenamingProfile(ScanProfile.DisplayName, _displayName.Text);
        }
        _scanProfile = GetUpdatedScanProfile();
        return true;
    }

    private ScanProfile GetUpdatedScanProfile()
    {
        var pageSize = (PageSizeListItem) _pageSize.SelectedValue;
        return new ScanProfile
        {
            Version = ScanProfile.CURRENT_VERSION,

            Device = ScanProfileDevice.FromScanDevice(_deviceSelectorWidget.Choice.Device),
            Caps = ScanProfile.Caps,
            IsDefault = _isDefault,
            DriverName = DeviceDriver.ToString().ToLowerInvariant(),
            DisplayName = _displayName.Text,
            IconID = 0,
            MaxQuality = ScanProfile.MaxQuality,
            UseNativeUI = _nativeUi.Checked,

            AfterScanScale = _scale.SelectedItem,
            BitDepth = _bitDepth.SelectedItem,
            Brightness = _brightnessSlider.IntValue,
            Contrast = _contrastSlider.IntValue,
            PageAlign = _horAlign.SelectedItem,
            PageSize = pageSize.Type,
            CustomPageSizeName = pageSize.CustomName,
            CustomPageSize = pageSize.CustomDimens,
            Resolution = new ScanResolution { Dpi = _resolution.SelectedItem },
            PaperSource = _paperSource.SelectedItem,

            EnableAutoSave = _enableAutoSave.IsChecked(),
            AutoSaveSettings = ScanProfile.AutoSaveSettings,
            Quality = ScanProfile.Quality,
            BrightnessContrastAfterScan = ScanProfile.BrightnessContrastAfterScan,
            AutoDeskew = ScanProfile.AutoDeskew,
            WiaOffsetWidth = ScanProfile.WiaOffsetWidth,
            WiaRetryOnFailure = ScanProfile.WiaRetryOnFailure,
            WiaDelayBetweenScans = ScanProfile.WiaDelayBetweenScans,
            WiaDelayBetweenScansSeconds = ScanProfile.WiaDelayBetweenScansSeconds,
            WiaVersion = ScanProfile.WiaVersion,
            ForcePageSize = ScanProfile.ForcePageSize,
            ForcePageSizeCrop = ScanProfile.ForcePageSizeCrop,
            FlipDuplexedPages = ScanProfile.FlipDuplexedPages,
            TwainImpl = ScanProfile.TwainImpl,
            TwainProgress = ScanProfile.TwainProgress,

            ExcludeBlankPages = ScanProfile.ExcludeBlankPages,
            BlankPageWhiteThreshold = ScanProfile.BlankPageWhiteThreshold,
            BlankPageCoverageThreshold = ScanProfile.BlankPageCoverageThreshold
        };
    }

    private void PredefinedSettings_CheckedChanged(object? sender, EventArgs e)
    {
        UpdateEnabledControls();
    }

    private void NativeUi_CheckedChanged(object? sender, EventArgs e)
    {
        UpdateEnabledControls();
    }

    private void UpdateEnabledControls()
    {
        if (!_suppressChangeEvent)
        {
            _suppressChangeEvent = true;

            bool canUseNativeUi = DeviceDriver is Driver.Wia or Driver.Twain;
            bool locked = ScanProfile.IsLocked;
            bool deviceLocked = ScanProfile.IsDeviceLocked;
            bool settingsEnabled = !locked && (_predefinedSettings.Checked || !canUseNativeUi);

            _displayName.Enabled = !locked;
            _deviceSelectorWidget.Enabled = !deviceLocked;
            _predefinedSettings.Enabled = _nativeUi.Enabled = !locked;
            _nativeUiVis.IsVisible = _deviceSelectorWidget.Choice.Device == null || canUseNativeUi;

            _paperSource.Enabled = settingsEnabled;
            _resolution.Enabled = settingsEnabled;
            _pageSize.Enabled = settingsEnabled;
            _bitDepth.Enabled = settingsEnabled;
            _horAlign.Enabled = settingsEnabled;
            _scale.Enabled = settingsEnabled;
            _brightnessSlider.Enabled = settingsEnabled;
            _contrastSlider.Enabled = settingsEnabled;

            _enableAutoSave.Enabled = !locked && !Config.Get(c => c.DisableAutoSave);
            _autoSaveSettings.Enabled = _enableAutoSave.IsChecked();
            _autoSaveSettings.Visible = !locked && !Config.Get(c => c.DisableAutoSave);

            _advanced.Enabled = !locked;

            _suppressChangeEvent = false;
        }
    }

    private int _lastPageSizeIndex = -1;
    private PageSizeListItem? _lastPageSizeItem;

    private void PaperSource_SelectedItemChanged(object? sender, EventArgs e)
    {
        if (_suppressChangeEvent) return;
        UpdateUiForCaps();
    }

    private void PageSize_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressPageSizeEvent) return;

        if (_pageSize.SelectedIndex == _pageSize.Items.Count - 1)
        {
            if (_lastPageSizeItem == null)
            {
                Log.Error("Expected last page size to be set");
                return;
            }
            // "Custom..." selected
            var form = FormFactory.Create<PageSizeForm>();
            form.PageSizeDimens = _lastPageSizeItem.Type == ScanPageSize.Custom
                ? _lastPageSizeItem.CustomDimens
                : _lastPageSizeItem.Type.PageDimensions();
            form.ShowModal();
            if (form.Result)
            {
                UpdatePageSizeList();
                SelectCustomPageSize(form.PageSizeName!, form.PageSizeDimens!);
            }
            else
            {
                _pageSize.SelectedIndex = _lastPageSizeIndex;
            }
        }
        _lastPageSizeIndex = _pageSize.SelectedIndex;
        _lastPageSizeItem = (PageSizeListItem) _pageSize.SelectedValue;
    }

    private void AutoSaveSettings_LinkClicked(object? sender, EventArgs eventArgs)
    {
        if (Config.Get(c => c.DisableAutoSave))
        {
            return;
        }
        var form = FormFactory.Create<AutoSaveSettingsForm>();
        ScanProfile.DriverName = DeviceDriver.ToString().ToLowerInvariant();
        form.ScanProfile = ScanProfile;
        form.ShowModal();
    }

    private void Advanced_Click(object? sender, EventArgs e)
    {
        var form = FormFactory.Create<AdvancedProfileForm>();
        ScanProfile.DriverName = DeviceDriver.ToString().ToLowerInvariant();
        ScanProfile.BitDepth = _bitDepth.SelectedItem;
        form.ScanProfile = ScanProfile;
        form.ShowModal();
    }

    private void EnableAutoSave_CheckedChanged(object? sender, EventArgs e)
    {
        if (!_suppressChangeEvent)
        {
            if (_enableAutoSave.IsChecked())
            {
                _autoSaveSettings.Enabled = true;
                var form = FormFactory.Create<AutoSaveSettingsForm>();
                form.ScanProfile = ScanProfile;
                form.ShowModal();
                if (!form.Result)
                {
                    _enableAutoSave.Checked = false;
                }
            }
        }
        _autoSaveSettings.Enabled = _enableAutoSave.IsChecked();
    }

    private class PageSizeListItem : IListItem
    {
        public string Text { get; set; } = null!;

        public string Key => Text;

        public ScanPageSize Type { get; set; }

        public string? CustomName { get; set; }

        public PageDimensions? CustomDimens { get; set; }
    }
}