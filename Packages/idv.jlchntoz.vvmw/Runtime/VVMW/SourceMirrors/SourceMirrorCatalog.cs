using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.UdonNetworkCalling;

namespace JLChnToZ.VRC.VVMW {
    /// <summary>
    /// Local catalog foundation. Context/snapshots must be supplied explicitly;
    /// this is not a shared playback identity provider or a publication service.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    [DisallowMultipleComponent]
    [AddComponentMenu("VizVid/Source Mirror Catalog (Local)")]
    public class SourceMirrorCatalog : UdonSharpBehaviour {
        public SourceMirrorCatalogAuthority authority;
        public const int Accepted = 0, Unchanged = 1, NotContextReady = 2,
            InvalidContext = 3, InvalidSnapshot = 4, UnsupportedSchema = 5,
            PayloadTooLarge = 6, CatalogChanged = 7, PlaybackChanged = 8,
            VersionConflict = 9, WriterChanged = 10, UnknownSource = 11,
            Unavailable = 12, Disabled = 13, TransportRejected = 14,
            LocalOnly = 15, UnsupportedPlatform = 16, RevisionExhausted = 17;
        public const int MaxSources = 3, DraftBudget = 12288, SnapshotBudget = 65536;
        string contextPlaybackId = "";
        VRCUrl contextPc, contextMobile;
        byte contextBackend;
        bool hasSnapshot;
        int result = NotContextReady;
        string writer = "", version = "", playback = "", film = "", compatibility = "";
        long revision;
        VRCUrl pc, mobile;
        byte backend;
        string[] ids = new string[0], labels = new string[0];
        VRCUrl[] urls = new VRCUrl[0];
        SourceMirrorController selectionObserver;
        bool networkManaged;

        public int Result => result;
        public bool NetworkPublicationEnabled => Utilities.IsValid(authority) && authority.IsReadyForPublication;
        public bool IsNetworkManaged => networkManaged || Utilities.IsValid(authority);
        public bool HasLocalContext => !string.IsNullOrEmpty(contextPlaybackId);
        public string ContextPlaybackId => contextPlaybackId;
        public VRCUrl ContextCanonical => contextPc;
        public VRCUrl ContextMobileCanonical => contextMobile;
        public byte ContextBackend => contextBackend;
        public string Version => version;
        public string FilmId => film;
        public string CompatibilityTag => compatibility;
        public long Revision => revision;
        public bool HasAcceptedSnapshot => hasSnapshot;
        public string SnapshotPlaybackId => playback;
        public VRCUrl SnapshotCanonical => pc;
        public VRCUrl SnapshotMobileCanonical => mobile;
        public byte SnapshotBackend => backend;
        public bool IsCurrent => enabled && gameObject.activeInHierarchy && hasSnapshot && HasLocalContext &&
            playback == contextPlaybackId && SameKey(pc, mobile, backend, contextPc, contextMobile, contextBackend) &&
            (!IsNetworkManaged || (Utilities.IsValid(authority) && authority.MatchesCoreContext(playback, pc, mobile, backend)));
        public int SourceCount => IsCurrent ? ids.Length : 0;

        bool AllowLocalMutation() {
            if (NetworkCalling.InNetworkCall) { result = LocalOnly; return false; }
            if (!enabled || !gameObject.activeInHierarchy) { result = Disabled; return false; }
            return true;
        }

        bool AllowFoundationWrite() {
            if (!AllowLocalMutation()) return false;
            if (IsNetworkManaged) { result = LocalOnly; return false; }
            return true;
        }

        // T123 will provide an authoritative Core binding. Supplying a token here
        // does not make it a synchronized identity; there is deliberately no watcher.
        public bool BindLocalContext(string playbackId, VRCUrl canonicalPc, VRCUrl canonicalMobile, byte playerType) {
            if (!AllowFoundationWrite()) return false;
            return BindContext(playbackId, canonicalPc, canonicalMobile, playerType);
        }

        bool BindContext(string playbackId, VRCUrl canonicalPc, VRCUrl canonicalMobile, byte playerType) {
            if (!ValidToken(playbackId) || VRCUrl.IsNullOrEmpty(canonicalPc) || playerType == 0) {
                result = InvalidContext; return false;
            }
            VRCUrl effectiveMobile = VRCUrl.IsNullOrEmpty(canonicalMobile) ? canonicalPc : canonicalMobile;
            if (UrlBytes(canonicalPc) + UrlBytes(effectiveMobile) > SnapshotBudget) {
                result = PayloadTooLarge; return false;
            }
            if (contextPlaybackId == playbackId) {
                if (!SameKey(contextPc, contextMobile, contextBackend, canonicalPc, effectiveMobile, playerType)) {
                    result = InvalidContext; return false;
                }
                result = Unchanged; return true;
            }
            contextPlaybackId = playbackId;
            contextPc = canonicalPc; contextMobile = effectiveMobile; contextBackend = playerType;
            NotifySelectionContext();
            result = Accepted; return true;
        }

        public void _ClearLocalContext() {
            if (!AllowFoundationWrite()) return;
            ClearContext();
        }

        void ClearContext() {
            contextPlaybackId = ""; contextPc = contextMobile = null; contextBackend = 0;
            NotifySelectionContext();
            result = NotContextReady; // Retain the immutable last accepted read cache.
        }

        public bool CommitLocalDraft(string expectedVersion, string expectedPlaybackId, string filmId,
            string compatibilityTag, string[] sourceIds, string[] sourceLabels, VRCUrl[] sourceUrls) {
            if (!AllowFoundationWrite() || !ValidateExpected(expectedVersion, expectedPlaybackId)) return false;
            if (!ValidateSources(filmId, compatibilityTag, sourceIds, sourceLabels, sourceUrls)) return false;
            int bytes = PayloadBytes(filmId, compatibilityTag, sourceIds, sourceLabels, sourceUrls) +
                TextBytes(expectedVersion) + TextBytes(expectedPlaybackId);
            if (bytes > DraftBudget) { result = PayloadTooLarge; return false; }
            if (revision == long.MaxValue) { result = RevisionExhausted; return false; }
            return AcceptLocalSnapshot(1, hasSnapshot ? writer : "local-foundation", revision + 1,
                expectedVersion, expectedPlaybackId, filmId, compatibilityTag, sourceIds, sourceLabels, sourceUrls);
        }

        // Local validation/copy entry only: no NetworkCallable, UdonSynced,
        // ownership change, serialization, URL load or claim of remote acceptance.
        public bool AcceptLocalSnapshot(int schemaVersion, string writerFence, long catalogRevision,
            string expectedVersion, string playbackId, string filmId, string compatibilityTag,
            string[] sourceIds, string[] sourceLabels, VRCUrl[] sourceUrls) {
            if (!AllowFoundationWrite()) return false;
            return AcceptSnapshot(schemaVersion, writerFence, catalogRevision, expectedVersion, playbackId,
                filmId, compatibilityTag, sourceIds, sourceLabels, sourceUrls, false);
        }

        bool AcceptSnapshot(int schemaVersion, string writerFence, long catalogRevision,
            string expectedVersion, string playbackId, string filmId, string compatibilityTag,
            string[] sourceIds, string[] sourceLabels, VRCUrl[] sourceUrls, bool newEpoch) {
            if (!ValidateExpected(expectedVersion, playbackId)) return false;
            if (schemaVersion != 1) { result = UnsupportedSchema; return false; }
            if (!ValidToken(writerFence) || writerFence.Length > 96 || catalogRevision <= 0) { result = InvalidSnapshot; return false; }
            if (!ValidateSources(filmId, compatibilityTag, sourceIds, sourceLabels, sourceUrls)) return false;
            int bytes = PayloadBytes(filmId, compatibilityTag, sourceIds, sourceLabels, sourceUrls) +
                TextBytes(writerFence) + TextBytes(playbackId) + UrlBytes(contextPc) + UrlBytes(contextMobile);
            if (bytes > SnapshotBudget) { result = PayloadTooLarge; return false; }
            bool writerChanged = hasSnapshot && writerFence != writer;
            if (writerChanged && !newEpoch) { result = WriterChanged; return false; }
            if (hasSnapshot && !writerChanged && catalogRevision < revision) { result = CatalogChanged; return false; }
            if (hasSnapshot && !writerChanged && catalogRevision == revision) {
                bool same = playback == playbackId && film == filmId && compatibility == compatibilityTag &&
                    SameKey(pc, mobile, backend, contextPc, contextMobile, contextBackend) &&
                    SameSources(sourceIds, sourceLabels, sourceUrls);
                result = same ? Unchanged : VersionConflict;
                return same;
            }
            if (hasSnapshot) {
                bool sameFilmKey = SameKey(pc, mobile, backend, contextPc, contextMobile, contextBackend);
                if ((sameFilmKey && film != filmId) || (!sameFilmKey && film == filmId)) {
                    result = InvalidSnapshot; return false;
                }
                if (sameFilmKey && !PreservesIds(sourceIds, sourceUrls)) {
                    result = InvalidSnapshot; return false;
                }
            }
            // Allocate/copy everything before publishing any accepted field.
            string[] copiedIds = CopyStrings(sourceIds), copiedLabels = CopyStrings(sourceLabels);
            VRCUrl[] copiedUrls = CopyUrls(sourceUrls);
            writer = writerFence; revision = catalogRevision;
            version = writerFence + ":" + catalogRevision;
            playback = playbackId; film = filmId; compatibility = compatibilityTag;
            pc = contextPc; mobile = contextMobile; backend = contextBackend;
            ids = copiedIds; labels = copiedLabels; urls = copiedUrls;
            hasSnapshot = true; result = Accepted;
            NotifySelectionContext();
            return true;
        }

        // Only the configured package arbiter opens this synchronous apply scope.
        // Never defer an authenticated RPC just to discard NetworkCalling context.
        bool Trusted(SourceMirrorCatalogAuthority source) {
            if (!enabled || !gameObject.activeInHierarchy || !Utilities.IsValid(authority) ||
                source != authority || !source.IsApplyingCatalog) return false;
            networkManaged = true;
            return true;
        }

        public bool BindSharedContext(SourceMirrorCatalogAuthority source, string playbackId,
            VRCUrl canonicalPc, VRCUrl canonicalMobile, byte playerType) =>
            Trusted(source) && BindContext(playbackId, canonicalPc, canonicalMobile, playerType);

        public void ClearSharedContext(SourceMirrorCatalogAuthority source) {
            if (Trusted(source)) ClearContext();
        }

        public bool ValidateSharedDraft(SourceMirrorCatalogAuthority source, string expectedVersion,
            string playbackId, string filmId, string tag, string[] sourceIds, string[] sourceLabels, VRCUrl[] sourceUrls) {
            if (!Trusted(source) || !ValidateExpected(expectedVersion, playbackId) ||
                !ValidateSources(filmId, tag, sourceIds, sourceLabels, sourceUrls)) return false;
            if (PayloadBytes(filmId, tag, sourceIds, sourceLabels, sourceUrls) +
                TextBytes(expectedVersion) + TextBytes(playbackId) > DraftBudget) { result = PayloadTooLarge; return false; }
            if (hasSnapshot) {
                bool sameFilmKey = SameKey(pc, mobile, backend, contextPc, contextMobile, contextBackend);
                if ((sameFilmKey && film != filmId) || (!sameFilmKey && film == filmId) ||
                    (sameFilmKey && !PreservesIds(sourceIds, sourceUrls))) { result = InvalidSnapshot; return false; }
            }
            result = Accepted; return true;
        }

        public bool AcceptSharedSnapshot(SourceMirrorCatalogAuthority source, string writerFence,
            long catalogRevision, string playbackId, string filmId, string tag,
            string[] sourceIds, string[] sourceLabels, VRCUrl[] sourceUrls, bool newEpoch) =>
            Trusted(source) && AcceptSnapshot(1, writerFence, catalogRevision, version, playbackId,
                filmId, tag, sourceIds, sourceLabels, sourceUrls, newEpoch);

        public bool RegisterLocalSelectionObserver(SourceMirrorController observer) {
            if (!AllowLocalMutation() || !Utilities.IsValid(observer) || observer.sourceCatalog != this) return false;
            if (Utilities.IsValid(selectionObserver) && selectionObserver != observer) return false;
            selectionObserver = observer;
            return true;
        }

        void NotifySelectionContext() {
            if (Utilities.IsValid(selectionObserver) && selectionObserver.sourceCatalog == this)
                selectionObserver.CatalogContextChanged(this);
        }

        bool ValidateExpected(string expectedVersion, string expectedPlaybackId) {
            if (!HasLocalContext) { result = NotContextReady; return false; }
            if (expectedPlaybackId != contextPlaybackId) { result = PlaybackChanged; return false; }
            if (expectedVersion != version) { result = CatalogChanged; return false; }
            return true;
        }

        bool ValidateSources(string filmId, string compatibilityTag, string[] sourceIds,
            string[] sourceLabels, VRCUrl[] sourceUrls) {
            if (!ValidId(filmId) || string.IsNullOrEmpty(compatibilityTag) || compatibilityTag.Length > 128 ||
                sourceIds == null || sourceLabels == null || sourceUrls == null ||
                sourceIds.Length < 1 || sourceIds.Length > MaxSources ||
                sourceLabels.Length != sourceIds.Length || sourceUrls.Length != sourceIds.Length) {
                result = InvalidSnapshot; return false;
            }
            if (VRCUrl.IsNullOrEmpty(sourceUrls[0]) || !sourceUrls[0].Equals(contextPc)) {
                result = InvalidSnapshot; return false;
            }
            for (int i = 0; i < sourceIds.Length; i++) {
                if (!ValidId(sourceIds[i]) || sourceLabels[i] == null || sourceLabels[i].Length > 128 ||
                    VRCUrl.IsNullOrEmpty(sourceUrls[i])) { result = InvalidSnapshot; return false; }
                for (int j = 0; j < i; j++) {
                    if (sourceIds[i] == sourceIds[j] || sourceUrls[i].Equals(sourceUrls[j])) {
                        result = InvalidSnapshot; return false;
                    }
                }
            }
            return true;
        }

        bool PreservesIds(string[] sourceIds, VRCUrl[] sourceUrls) {
            if (sourceIds[0] != ids[0]) return false;
            for (int i = 0; i < sourceUrls.Length; i++)
                for (int j = 0; j < urls.Length; j++)
                    if (sourceUrls[i].Equals(urls[j]) && sourceIds[i] != ids[j]) return false;
            return true;
        }

        bool SameSources(string[] otherIds, string[] otherLabels, VRCUrl[] otherUrls) {
            if (ids.Length != otherIds.Length) return false;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] != otherIds[i] || labels[i] != otherLabels[i] || !urls[i].Equals(otherUrls[i])) return false;
            return true;
        }

        public VRCUrl ResolveSource(string sourceId, string expectedVersion, string expectedPlaybackId) {
            if (!AllowLocalMutation() || !ValidateExpected(expectedVersion, expectedPlaybackId)) return null;
            if (!IsCurrent) { result = NotContextReady; return null; }
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == sourceId) { result = Accepted; return urls[i]; }
            result = UnknownSource; return null;
        }

        public string GetSourceId(int index) => IsCurrent && index >= 0 && index < ids.Length ? ids[index] : "";
        public string GetSourceLabel(int index) => IsCurrent && index >= 0 && index < labels.Length ? labels[index] : "";
        public VRCUrl GetSourceUrl(int index) => IsCurrent && index >= 0 && index < urls.Length ? urls[index] : null;
        public string[] GetSourceIds() => CopyStrings(ids);
        public string[] GetSourceLabels() => CopyStrings(labels);
        public VRCUrl[] GetSourceUrls() => CopyUrls(urls);

        bool SameKey(VRCUrl aPc, VRCUrl aMobile, byte aBackend, VRCUrl bPc, VRCUrl bMobile, byte bBackend) =>
            aBackend == bBackend && !VRCUrl.IsNullOrEmpty(aPc) && !VRCUrl.IsNullOrEmpty(bPc) && aPc.Equals(bPc) &&
            !VRCUrl.IsNullOrEmpty(aMobile) && !VRCUrl.IsNullOrEmpty(bMobile) && aMobile.Equals(bMobile);

        bool ValidId(string value) {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')) return false;
            }
            return true;
        }

        bool ValidToken(string value) {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            for (int i = 0; i < value.Length; i++) if (value[i] < 32 || value[i] == 127) return false;
            return true;
        }

        int PayloadBytes(string filmId, string tag, string[] sourceIds, string[] sourceLabels, VRCUrl[] sourceUrls) {
            // Conservative local envelope, not a claim about SDK wire byteCount.
            int bytes = 512 + TextBytes(filmId) + TextBytes(tag);
            for (int i = 0; i < sourceIds.Length; i++)
                bytes += 12 + TextBytes(sourceIds[i]) + TextBytes(sourceLabels[i]) + UrlBytes(sourceUrls[i]);
            return bytes;
        }

        int UrlBytes(VRCUrl value) => VRCUrl.IsNullOrEmpty(value) ? 0 : TextBytes(value.Get());
        int TextBytes(string value) {
            if (value == null) return 0;
            if (value.Length > SnapshotBudget) return SnapshotBudget + 1;
            int bytes = 0;
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (c <= 0x7F) bytes++;
                else if (c <= 0x7FF) bytes += 2;
                else if (c >= 0xD800 && c <= 0xDBFF && i + 1 < value.Length &&
                    value[i + 1] >= 0xDC00 && value[i + 1] <= 0xDFFF) { bytes += 4; i++; }
                else bytes += 3;
                if (bytes > SnapshotBudget) return SnapshotBudget + 1;
            }
            return bytes;
        }

        string[] CopyStrings(string[] source) {
            string[] copy = new string[source.Length];
            for (int i = 0; i < source.Length; i++) copy[i] = source[i];
            return copy;
        }
        VRCUrl[] CopyUrls(VRCUrl[] source) {
            VRCUrl[] copy = new VRCUrl[source.Length];
            for (int i = 0; i < source.Length; i++) copy[i] = source[i];
            return copy;
        }
    }
}
