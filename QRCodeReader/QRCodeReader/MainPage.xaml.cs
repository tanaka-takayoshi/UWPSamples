using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using ZXing;
using Panel = Windows.Devices.Enumeration.Panel;

// 空白ページのアイテム テンプレートについては、http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409 を参照してください

namespace QRCodeReader
{
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly DisplayInformation displayInformation = DisplayInformation.GetForCurrentView();
        private DisplayOrientations displayOrientation = DisplayOrientations.Portrait;

        // Rotation metadata to apply to the preview stream (MF_MT_VIDEO_ROTATION)
        // Reference: http://msdn.microsoft.com/en-us/library/windows/apps/xaml/hh868174.aspx
        private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        private readonly DisplayRequest displayRequest = new DisplayRequest();

        private readonly SystemMediaTransportControls systemMediaControls = SystemMediaTransportControls.GetForCurrentView();

        private MediaCapture mediaCapture;
        private bool isInitialized = false;
        private bool isPreviewing = false;

        private bool mirroringPreview = false;
        private bool externalCamera = false;

        private Timer timer;

        public MainPage()
        {
            InitializeComponent();

            NavigationCacheMode = NavigationCacheMode.Required;

            // Useful to know when to initialize/clean up the camera
            Application.Current.Suspending += (sender, e) =>
            {
                if (Frame.CurrentSourcePageType == GetType())
                {
                    var deferral = e.SuspendingOperation.GetDeferral();

                    CleanupCameraAsync().FireAndForget();

                    displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;

                    deferral.Complete();
                }
            };
            Application.Current.Resuming += (sender, o) =>
            {
                if (Frame.CurrentSourcePageType == GetType())
                {
                    displayOrientation = displayInformation.CurrentOrientation;
                    displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;

                    InitializeCameraAsync().FireAndForget();
                }
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
           displayOrientation = displayInformation.CurrentOrientation;
           displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;

            InitializeCameraAsync().FireAndForget();
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            CleanupCameraAsync().FireAndForget();

            displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;
        }

        private void SystemMediaControls_PropertyChanged(SystemMediaTransportControls sender, SystemMediaTransportControlsPropertyChangedEventArgs args)
        {
            Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                // Only handle this event if this page is currently being displayed
                if (args.Property == SystemMediaTransportControlsProperty.SoundLevel && Frame.CurrentSourcePageType == typeof(MainPage))
                {
                    // Check to see if the app is being muted. If so, it is being minimized.
                    // Otherwise if it is not initialized, it is being brought into focus.
                    if (sender.SoundLevel == SoundLevel.Muted)
                    {
                        await CleanupCameraAsync();
                    }
                    else if (!isInitialized)
                    {
                        await InitializeCameraAsync();
                    }
                }
            }).FireAndForget();
        }

        private void DisplayInformation_OrientationChanged(DisplayInformation sender, object args)
        {
            displayOrientation = sender.CurrentOrientation;

            if (isPreviewing)
            {
                SetPreviewRotationAsync().FireAndForget();
            }
        }

        private void MediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            Debug.WriteLine("MediaCapture_Failed: (0x{0:X}) {1}", errorEventArgs.Code, errorEventArgs.Message);

            CleanupCameraAsync().FireAndForget();
        }

        private async Task InitializeCameraAsync()
        {
            Debug.WriteLine("InitializeCameraAsync");

            if (mediaCapture == null)
            {
                // Attempt to get the back camera if one is available, but use any camera device if not
                var cameraDevice = await FindCameraDeviceByPanelAsync(Panel.Back);

                if (cameraDevice == null)
                {
                    Debug.WriteLine("No camera device found!");
                    return;
                }

                // Create MediaCapture and its settings
                mediaCapture = new MediaCapture();

                // Register for a notification when something goes wrong
                mediaCapture.Failed += MediaCapture_Failed;

                var settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id };

                // Initialize MediaCapture
                try
                {
                    await mediaCapture.InitializeAsync(settings);
                    isInitialized = true;
                }
                catch (UnauthorizedAccessException)
                {
                    Debug.WriteLine("The app was denied access to the camera");
                }

                // If initialization succeeded, start the preview
                if (isInitialized)
                {
                    // Figure out where the camera is located
                    if (cameraDevice.EnclosureLocation == null || cameraDevice.EnclosureLocation.Panel == Panel.Unknown)
                    {
                        // No information on the location of the camera, assume it's an external camera, not integrated on the device
                        externalCamera = true;
                    }
                    else
                    {
                        // Camera is fixed on the device
                        externalCamera = false;

                        // Only mirror the preview if the camera is on the front panel
                        mirroringPreview = (cameraDevice.EnclosureLocation.Panel == Panel.Front);
                    }

                    await StartPreviewAsync();
                }
            }
        }

        /// <summary>
        /// Starts the preview and adjusts it for for rotation and mirroring after making a request to keep the screen on and unlocks the UI
        /// </summary>
        /// <returns></returns>
        private async Task StartPreviewAsync()
        {
            Debug.WriteLine("StartPreviewAsync");

            // Prevent the device from sleeping while the preview is running
            displayRequest.RequestActive();

            // Register to listen for media property changes
            systemMediaControls.PropertyChanged += SystemMediaControls_PropertyChanged;

            // Set the preview source in the UI and mirror it if necessary
            PreviewControl.Source = mediaCapture;
            PreviewControl.FlowDirection = mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            // Start the preview
            await mediaCapture.StartPreviewAsync();
            isPreviewing = true;

            // Initialize the preview to the current orientation
            if (isPreviewing)
            {
                await SetPreviewRotationAsync();
            }

            // Enable / disable the button depending on the preview state
            timer = new Timer(_ =>
            {
                TryDecodePreviewAsync().FireAndForget();
            }, null,TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Gets the current orientation of the UI in relation to the device and applies a corrective rotation to the preview
        /// </summary>
        private async Task SetPreviewRotationAsync()
        {
            // Only need to update the orientation if the camera is mounted on the device
            if (externalCamera) return;

            // Calculate which way and how far to rotate the preview
            int rotationDegrees = ConvertDisplayOrientationToDegrees(displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored
            if (mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
            var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
            props.Properties.Add(RotationKey, rotationDegrees);
            await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
        }

        /// <summary>
        /// Stops the preview and deactivates a display request, to allow the screen to go into power saving modes, and locks the UI
        /// </summary>
        /// <returns></returns>
        private async Task StopPreviewAsync()
        {
            isPreviewing = false;
            await mediaCapture.StopPreviewAsync();

            // Use the dispatcher because this method is sometimes called from non-UI threads
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                PreviewControl.Source = null;

                // Allow the device to sleep now that the preview is stopped
                displayRequest.RequestRelease();
            });
        }

        /// <summary>
        /// Gets the current preview frame as a SoftwareBitmap, displays its properties in a TextBlock, and can optionally display the image
        /// in the UI and/or save it to disk as a jpg
        /// </summary>
        /// <returns></returns>
        private async Task TryDecodePreviewAsync()
        {
            await mediaCapture.VideoDeviceController.FocusControl.FocusAsync();
            // Get information about the preview
            var previewProperties = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;
            if (previewProperties == null)
                return;
            // このVideoFrameのFormatとBarcodeReaderのフォーマットで一致するのがGray8しかない
            var videoFrame = new VideoFrame(BitmapPixelFormat.Gray8, (int)previewProperties.Width, (int)previewProperties.Height);

            // Capture the preview frame
            using (var currentFrame = await mediaCapture.GetPreviewFrameAsync(videoFrame))
            {
                // 結果フレームを取得
                var previewFrame = currentFrame.SoftwareBitmap;
                // 結果をbyte配列に変換。DecodeメソッドはWriteableBitmapも受け付けるが、
                // こちらはUIスレッド上でないと生成できないのであきらめる。
                var buffer = new byte[4 * previewFrame.PixelWidth * previewFrame.PixelHeight];
                previewFrame.CopyToBuffer(buffer.AsBuffer());
                var barcodeReader = new BarcodeReader {AutoRotate = true};
                var r = barcodeReader.Decode(buffer, previewFrame.PixelWidth, previewFrame.PixelHeight, BitmapFormat.Gray8);
                Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    FrameInfoTextBlock.Text = string.Format("{0}x{1} {2}", previewFrame.PixelWidth, previewFrame.PixelHeight, previewFrame.BitmapPixelFormat);
                    ResultText.Text = r?.Text ?? "";
                }).FireAndForget();
            }
        }

        private async Task CleanupCameraAsync()
        {
            if (isInitialized)
            {
                if (isPreviewing)
                {
                    // The call to stop the preview is included here for completeness, but can be
                    // safely removed if a call to MediaCapture.Dispose() is being made later,
                    // as the preview will be automatically stopped at that point
                    await StopPreviewAsync();
                }

                isInitialized = false;
            }

            if (mediaCapture != null)
            {
                mediaCapture.Failed -= MediaCapture_Failed;
                mediaCapture.Dispose();
                mediaCapture = null;
            }

            timer?.Dispose();
        }

        private static async Task<DeviceInformation> FindCameraDeviceByPanelAsync(Panel desiredPanel)
        {
            // Get available devices for capturing pictures
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            // Get the desired camera by panel
            var desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

            // If there is no device mounted on the desired panel, return the first device found
            return desiredDevice ?? allVideoDevices.FirstOrDefault();
        }

        /// <summary>
        /// Converts the given orientation of the app on the screen to the corresponding rotation in degrees
        /// </summary>
        /// <param name="orientation">The orientation of the app on the screen</param>
        /// <returns>An orientation in degrees</returns>
        private static int ConvertDisplayOrientationToDegrees(DisplayOrientations orientation)
        {
            switch (orientation)
            {
                case DisplayOrientations.Portrait:
                    return 90;
                case DisplayOrientations.LandscapeFlipped:
                    return 180;
                case DisplayOrientations.PortraitFlipped:
                    return 270;
                default:
                    return 0;
            }
        }

        ///// <summary>
        ///// Saves a SoftwareBitmap to the Pictures library with the specified name
        ///// </summary>
        ///// <param name="bitmap"></param>
        ///// <returns></returns>
        //private static async Task SaveSoftwareBitmapAsync(SoftwareBitmap bitmap)
        //{
        //    var file = await KnownFolders.PicturesLibrary.CreateFileAsync("PreviewFrame.jpg", CreationCollisionOption.GenerateUniqueName);
        //    using (var outputStream = await file.OpenAsync(FileAccessMode.ReadWrite))
        //    {
        //        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);

        //        // Grab the data from the SoftwareBitmap
        //        encoder.SetSoftwareBitmap(bitmap);
        //        await encoder.FlushAsync();
        //    }
        //}
    }

    public static class TaslExtensions
    {
        public static void FireAndForget(this Task task)
        {
            task.ContinueWith(t => { Debug.WriteLine(t.Exception);}, TaskContinuationOptions.OnlyOnFaulted);
        }

        public static void FireAndForget(this IAsyncAction action)
        {
            action.AsTask().FireAndForget();
        }
    }
}
