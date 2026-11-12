using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Image;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.Components.Video;
using UdonSharp;
using JLChnToZ.VRC.Foundation;

namespace JLChnToZ.VRC.VVMW {
    /// <summary>
    /// A backend for loading and displaying images.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    [DisallowMultipleComponent]
    [AddComponentMenu("VizVid/Components/Image Viewer Handler")]
    [DefaultExecutionOrder(0)]
    public class ImageViewerHandler : AbstractMediaPlayerHandler {
        VRCImageDownloader loader;
        VRCImageDownloader heldImageLoaderForUfc;
        IVRCImageDownload currentImageDownloadForUfc;
        TextureInfo currentImageTextureInfoForUfc;
        bool issuingImageDownloadForUfc;
        bool currentImagePresentedForUfc;
        bool imageDestroyedForUfc;
        int imageLoadRevisionForUfc;
        bool loop, isPlaying;

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override bool IsActive {
            get => isActive;
            set {
                isActive = value;
                RetireImageDownloadsForUfc(false);
            }
        }

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override bool Loop {
            get => loop;
            set => loop = value;
        }

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override bool IsPlaying => isPlaying;

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override bool IsStatic => true;

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override void LoadUrl(VRCUrl url, bool reload) {
            if (imageDestroyedForUfc) return;
            if (url.Equals(currentUrl) && !reload && Utilities.IsValid(texture)) {
                isReady = true;
                if (isActive) core.OnVideoReady();
                return;
            }
            int revision = RetireImageDownloadsForUfc(true);
            if (revision != imageLoadRevisionForUfc) return;
            var imageLoader = new VRCImageDownloader();
            loader = imageLoader;
            var textureInfo = new TextureInfo();
            textureInfo.WrapModeU = TextureWrapMode.Clamp;
            textureInfo.WrapModeV = TextureWrapMode.Clamp;
            textureInfo.WrapModeW = TextureWrapMode.Clamp;
            textureInfo.GenerateMipMaps = core.enableMipmap;
            currentImageTextureInfoForUfc = textureInfo;
            currentUrl = url;
            texture = null;
            isReady = false;
            isPlaying = false;
            currentImageDownloadForUfc = null;
            issuingImageDownloadForUfc = true;
            var download = imageLoader.DownloadImage(url, null, (IUdonEventReceiver)this, textureInfo);
            if (revision != imageLoadRevisionForUfc || !object.ReferenceEquals(loader, imageLoader)) {
                // Synchronous callbacks can retire the loader before the SDK adds its handle.
                if (!object.ReferenceEquals(loader, imageLoader) && !object.ReferenceEquals(heldImageLoaderForUfc, imageLoader)) {
                    if (Utilities.IsValid(download)) download.Dispose();
                    imageLoader.Dispose();
                }
                return;
            }
            issuingImageDownloadForUfc = false;
            if (url.Equals(currentUrl) && currentImageDownloadForUfc == null)
                currentImageDownloadForUfc = download;
        }

        bool IsCurrentImageDownloadForUfc(IVRCImageDownload image) {
            if (!Utilities.IsValid(image) || !Utilities.IsValid(image.Url) || !image.Url.Equals(currentUrl)) return false;
            // InvalidURL can report synchronously before DownloadImage returns its handle.
            if (issuingImageDownloadForUfc && currentImageDownloadForUfc == null &&
                object.ReferenceEquals(currentImageTextureInfoForUfc, image.TextureInfo))
                currentImageDownloadForUfc = image;
            return object.ReferenceEquals(currentImageDownloadForUfc, image);
        }

        public override void OnImageLoadSuccess(IVRCImageDownload image) {
            if (!IsCurrentImageDownloadForUfc(image)) return;
            texture = image.Result;
            isReady = true;
            isPlaying = false;
            if (isActive) core.OnVideoReady();
        }

        public override void OnImageLoadError(IVRCImageDownload image) {
            if (!IsCurrentImageDownloadForUfc(image)) return;
            var imageError = image.Error;
            int revision = RetireImageDownloadsForUfc(true);
            if (revision == imageLoadRevisionForUfc && isActive) {
                var error = VideoError.Unknown;
                switch (imageError) {
                    case VRCImageDownloadError.DownloadError:
                    case VRCImageDownloadError.InvalidImage:
                        error = VideoError.PlayerError;
                        break;
                    case VRCImageDownloadError.InvalidURL:
                        error = VideoError.InvalidURL;
                        break;
                    case VRCImageDownloadError.AccessDenied:
                        error = VideoError.AccessDenied;
                        break;
                    case VRCImageDownloadError.TooManyRequests:
                        error = VideoError.RateLimited;
                        break;
                }
                core.OnVideoError(error);
            }
        }

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override void Play() {
            if (!isReady || !Utilities.IsValid(texture)) return;
            isPlaying = true;
            if (isActive) {
                int revision = imageLoadRevisionForUfc;
                core.OnVideoPlay();
                if (revision != imageLoadRevisionForUfc || !Utilities.IsValid(texture)) return;
                var previousImageLoader = heldImageLoaderForUfc;
                heldImageLoaderForUfc = null;
                currentImagePresentedForUfc = true;
                core._OnTextureChanged();
                if (Utilities.IsValid(previousImageLoader)) previousImageLoader.Dispose();
            }
        }

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override void Pause() => Play();

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override void Stop() {
            int revision = RetireImageDownloadsForUfc(false);
            if (revision == imageLoadRevisionForUfc && isActive) core.OnVideoEnd();
        }

        int RetireImageDownloadsForUfc(bool preservePresentedImage) {
            int revision = ++imageLoadRevisionForUfc;
            var retiringLoader = loader;
            var previousHeldLoader = heldImageLoaderForUfc;
            bool retainCurrent = preservePresentedImage && currentImagePresentedForUfc;
            // Retire callback identity before Dispose can invoke synchronous SDK callbacks.
            loader = null;
            currentImageDownloadForUfc = null;
            currentImageTextureInfoForUfc = null;
            issuingImageDownloadForUfc = false;
            currentImagePresentedForUfc = false;
            texture = null;
            currentUrl = null;
            isReady = false;
            isPlaying = false;
            if (preservePresentedImage) {
                // Core can still present A while B prepares. Keep at most that one loader.
                if (retainCurrent) {
                    heldImageLoaderForUfc = retiringLoader;
                    retiringLoader = previousHeldLoader;
                }
            } else
                heldImageLoaderForUfc = null;
            if (Utilities.IsValid(retiringLoader) && !object.ReferenceEquals(retiringLoader, heldImageLoaderForUfc))
                retiringLoader.Dispose();
            if (!preservePresentedImage && Utilities.IsValid(previousHeldLoader) && !object.ReferenceEquals(previousHeldLoader, retiringLoader))
                previousHeldLoader.Dispose();
            return revision;
        }

        void OnDestroy() {
            imageDestroyedForUfc = true;
            RetireImageDownloadsForUfc(false);
        }

#if COMPILER_UDONSHARP
        public
#else
        internal protected
#endif
        override int IsSupported(string urlStr) {
            int index = urlStr.IndexOf('#');
            if (index < 0) {
                index = urlStr.IndexOf('?');
                if (index < 0) index = urlStr.Length;
            }
            int startIndex = urlStr.LastIndexOf('.', index - 1) + 1;
            if (startIndex < 0 || startIndex >= index) return -1;
            switch (urlStr.Substring(startIndex, index - startIndex).ToLower()) {
                case "png":
                case "jpg":
                case "jpeg":
                    return 1;
            }
            return -1;
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        protected override void PreProcess() {
            if (Utilities.IsValid(applyTurstedUrl)) applyTurstedUrl(TrustedUrlTypes.ImageUrl, ref trustedUrlDomains);
        }
#endif
    }
}
