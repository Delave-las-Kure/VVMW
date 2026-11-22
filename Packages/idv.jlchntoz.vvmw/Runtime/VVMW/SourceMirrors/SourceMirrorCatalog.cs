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
            string[] sourceIds, string[] sourceLabels, VRCUrl[] sourceUrls, bool newEpoch, bool checkContinuity = true) {
            if (!ValidateExpected(expectedVersion, playbackId)) return false;
            if (schemaVersion != 1) { result = UnsupportedSchema; return false; }
            if (!ValidToken(writerFence) || writerFence.Length > 96 || catalogRevision <= 0) { result = InvalidSnapshot; return false; }
            // An earlier room version could save endpoint aliases. Keep that list readable
            // until an editor removes the duplicate. New drafts always use strict validation.
            if (!ValidateSources(filmId, compatibilityTag, sourceIds, sourceLabels, sourceUrls, IsNetworkManaged)) return false;
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
            if (hasSnapshot && checkContinuity) {
                bool sameFilmKey = SameKey(pc, mobile, backend, contextPc, contextMobile, contextBackend);
                if ((sameFilmKey && film != filmId) || (!sameFilmKey && film == filmId)) {
                    result = InvalidSnapshot; return false;
                }
                if (sameFilmKey && !PreservesIds(sourceIds, sourceUrls, !IsNetworkManaged)) {
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
                    (sameFilmKey && !PreservesIds(sourceIds, sourceUrls, true))) { result = InvalidSnapshot; return false; }
            }
            result = Accepted; return true;
        }

        public bool AcceptSharedSnapshot(SourceMirrorCatalogAuthority source, string writerFence,
            long catalogRevision, string playbackId, string filmId, string tag,
            string[] sourceIds, string[] sourceLabels, VRCUrl[] sourceUrls, bool newEpoch, bool checkContinuity = true) =>
            Trusted(source) && AcceptSnapshot(1, writerFence, catalogRevision, version, playbackId,
                filmId, tag, sourceIds, sourceLabels, sourceUrls, newEpoch, checkContinuity);

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
            string[] sourceLabels, VRCUrl[] sourceUrls, bool allowLegacyAliases = false) {
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
                if (i > 0 && !allowLegacyAliases && !ValidMirrorHost(sourceUrls[i].Get())) {
                    result = InvalidSnapshot; return false;
                }
                for (int j = 0; j < i; j++) {
                    bool duplicate = allowLegacyAliases ? sourceUrls[i].Equals(sourceUrls[j]) :
                        SameSourceEndpoint(sourceUrls[i], sourceUrls[j]);
                    if (sourceIds[i] == sourceIds[j] || duplicate) {
                        result = InvalidSnapshot; return false;
                    }
                }
            }
            return true;
        }

        public bool SameSourceEndpoint(VRCUrl left, VRCUrl right) {
            if (VRCUrl.IsNullOrEmpty(left) || VRCUrl.IsNullOrEmpty(right)) return false;
            return left.Equals(right) || EndpointKey(left.Get()) == EndpointKey(right.Get());
        }

        char[] authorityEndChars = { '/', '?', '#' };
        string EndpointKey(string value) {
            int separator = value.IndexOf("://");
            if (separator <= 0) return value;
            string scheme = value.Substring(0, separator).ToLowerInvariant();
            int start = separator + 3;
            int end = value.IndexOfAny(authorityEndChars, start);
            if (end < 0) end = value.Length;
            string authorityPart = value.Substring(start, end - start);
            int hostStart = authorityPart.LastIndexOf('@') + 1;
            string hostPort = authorityPart.Substring(hostStart);
            bool http = scheme == "https" || scheme == "http";
            if (http) hostPort = NormalizeHttpPort(hostPort, scheme == "https" ? "443" : "80");
            // DNS aliases name the same server across media protocols. Only HTTP
            // normalizes the port and resource. Credentials and IPv6 zones stay literal.
            string hostKey = HostKey(hostPort);
            string resource = value.Substring(end);
            if (http) resource = NormalizeHttpResource(resource);
            return scheme + "://" + authorityPart.Substring(0, hostStart) + hostKey + resource;
        }

        public string NormalizeDnsSeparators(string host) {
            return host.Replace('\u3002', '.').Replace('\uFF0E', '.').Replace('\uFF61', '.');
        }

        bool ValidMirrorHost(string value) {
            int scheme = value.IndexOf("://");
            if (scheme <= 0) return true;
            int start = scheme + 3;
            int end = value.IndexOfAny(authorityEndChars, start);
            if (end < 0) end = value.Length;
            string address = value.Substring(start, end - start);
            string hostPort = address.Substring(address.LastIndexOf('@') + 1);
            if (hostPort.StartsWith("[")) return true;
            int port = hostPort.IndexOf(':');
            return ValidMirrorDnsHost(port < 0 ? hostPort : hostPort.Substring(0, port));
        }

        public bool ValidMirrorDnsHost(string host) {
            if (!ValidDnsLength(host)) return false;
            string address = NormalizeDnsSeparators(host);
            // A final DNS root dot does not exempt a numeric address from the octet limits.
            if (address.EndsWith(".")) address = address.Substring(0, address.Length - 1);
            return !System.Text.RegularExpressions.Regex.IsMatch(address, @"\A[0-9]+(?:\.[0-9]+){3}\z") || ValidIpv4(address);
        }

        public bool ValidIpv4(string address) {
            var octets = address.Split('.');
            if (octets.Length != 4) return false;
            foreach (var octet in octets)
                if (!System.Text.RegularExpressions.Regex.IsMatch(octet, @"\A[0-9]{1,3}\z") || int.Parse(octet) > 255) return false;
            return true;
        }

        public bool ValidDnsLength(string host) {
            if (string.IsNullOrEmpty(host)) return false;
            var ascii = AsciiDnsHost(host);
            int end = ascii.EndsWith(".") ? ascii.Length - 1 : ascii.Length;
            if (end == 0 || end > 253) return false;
            int length = 0;
            for (int i = 0; i < end; i++) {
                if (ascii[i] == '.') {
                    if (length == 0) return false;
                    length = 0;
                } else if (ascii[i] >= 128 || ++length > 63) return false;
            }
            return length > 0;
        }

        string HostKey(string hostPort) {
            if (hostPort.StartsWith("[")) {
                int close = hostPort.IndexOf(']');
                if (close < 0) return hostPort;
                int zone = hostPort.IndexOf('%');
                int end = zone > 0 && zone < close ? zone : close;
                string literal = hostPort.Substring(1, end - 1);
                return "[" + IPv6Key(literal.ToLowerInvariant()) + hostPort.Substring(end);
            }
            int colon = hostPort.LastIndexOf(':');
            string host = colon < 0 ? hostPort : hostPort.Substring(0, colon);
            host = AsciiDnsHost(host).ToLowerInvariant();
            return colon < 0 ? host : host + hostPort.Substring(colon);
        }

        string AsciiDnsHost(string host) {
            for (int i = 0; i < host.Length; i++) {
                if (host[i] < 128) continue;
                string[] labels = NormalizeDnsSeparators(host).Split('.');
                for (int j = 0; j < labels.Length; j++) labels[j] = AsciiLabel(labels[j]);
                return string.Join(".", labels);
            }
            return host;
        }

        // Compare literal addresses by their eight hextets; retain the scope and port in the caller.
        string IPv6Key(string literal) {
            int gap = literal.IndexOf("::");
            if (gap != literal.LastIndexOf("::")) return literal;
            if (literal.IndexOf('.') >= 0) {
                int lastColon = literal.LastIndexOf(':');
                if (lastColon < 0) return literal;
                string[] octets = literal.Substring(lastColon + 1).Split('.');
                if (octets.Length != 4) return literal;
                int[] bytes = new int[4];
                for (int i = 0; i < 4; i++) {
                    string octet = octets[i];
                    if (octet.Length == 0 || octet.Length > 3 || (octet.Length > 1 && octet[0] == '0')) return literal;
                    for (int j = 0; j < octet.Length; j++) {
                        int digit = octet[j] - '0';
                        if (digit < 0 || digit > 9) return literal;
                        bytes[i] = bytes[i] * 10 + digit;
                    }
                    if (bytes[i] > 255) return literal;
                }
                literal = literal.Substring(0, lastColon + 1) + (bytes[0] * 256 + bytes[1]).ToString("x") +
                    ":" + (bytes[2] * 256 + bytes[3]).ToString("x");
            }
            string head = gap < 0 ? literal : literal.Substring(0, gap);
            string tail = gap < 0 ? "" : literal.Substring(gap + 2);
            var left = head.Length == 0 ? new string[0] : head.Split(':');
            var right = tail.Length == 0 ? new string[0] : tail.Split(':');
            int missing = 8 - left.Length - right.Length;
            if (gap < 0 ? missing != 0 : missing < 1) return literal;
            var words = new string[8];
            for (int i = 0; i < 8; i++) {
                string word = i < left.Length ? left[i] : i >= 8 - right.Length ? right[i - (8 - right.Length)] : "0";
                if (word.Length == 0 || word.Length > 4) return literal;
                for (int j = 0; j < word.Length; j++) if (HexDigit(word[j]) < 0) return literal;
                words[i] = word.PadLeft(4, '0');
            }
            return string.Join(":", words);
        }

        // RFC 3492 encoder. Udon exposes string normalization, but not Uri or IdnMapping.
        // The 63-character label limit also bounds all delta arithmetic and loop counts.
        string AsciiLabel(string label) {
            if (label.Length == 0) return label;
            bool unicode = false;
            for (int i = 0; i < label.Length; i++) {
                char c = label[i];
                if (c >= 128) unicode = true;
                int scalar = c;
                if (char.IsSurrogate(c)) {
                    if (!char.IsSurrogatePair(label, i)) return label;
                    scalar = char.ConvertToUtf32(label, i++);
                }
                if ((scalar & 0xffff) >= 0xfffe || (scalar >= 0xfdd0 && scalar <= 0xfdef)) return label;
            }
            if (!unicode) return label;
            // Apply width/compatibility mapping before invariant case conversion. Keep the
            // code points selected by the player runtime (including its dotted-I mapping).
            string normalized = label.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant().Normalize();
            if (normalized.Length == 0 || normalized.Length > 126) return label;
            var points = new int[normalized.Length];
            string output = "";
            int count = 0;
            for (int i = 0; i < normalized.Length; i++) {
                int code = char.ConvertToUtf32(normalized, i);
                if (code > 65535) i++;
                if (count == 63) return label;
                points[count++] = code;
                if (code < 128) {
                    // Compatibility characters must not introduce a new URL delimiter.
                    if (!(code >= 'a' && code <= 'z') && !(code >= '0' && code <= '9') &&
                        code != '-' && code != '_') return label;
                    output += (char)code;
                }
            }
            int basic = output.Length, handled = basic;
            if (basic == count) return output;
            if (basic > 0) output += "-";
            int next = 128, delta = 0, bias = 72;
            const string digits = "abcdefghijklmnopqrstuvwxyz0123456789";
            while (handled < count) {
                int smallest = 0x110000;
                for (int i = 0; i < count; i++)
                    if (points[i] >= next && points[i] < smallest) smallest = points[i];
                delta += (smallest - next) * (handled + 1);
                next = smallest;
                for (int i = 0; i < count; i++) {
                    if (points[i] < next) delta++;
                    if (points[i] != next) continue;
                    int q = delta;
                    for (int k = 36; ; k += 36) {
                        int threshold = k <= bias ? 1 : k >= bias + 26 ? 26 : k - bias;
                        if (q < threshold) break;
                        output += digits[threshold + (q - threshold) % (36 - threshold)];
                        q = (q - threshold) / (36 - threshold);
                    }
                    output += digits[q];
                    handled++;
                    int skew = delta / (handled == basic + 1 ? 700 : 2);
                    skew += skew / handled;
                    bias = 0;
                    while (skew > 455) { skew /= 35; bias += 36; }
                    bias += 36 * skew / (skew + 38);
                    delta = 0;
                }
                delta++;
                next++;
            }
            return "xn--" + output;
        }

        int HexDigit(char c) {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return -1;
        }

        // Compare non-ASCII path characters using the bytes sent by an HTTP client.
        // Unescape only unreserved ASCII; leave delimiters and nested escapes intact.
        string UnescapePath(string path) {
            int first = path.Length, extra = 0;
            for (int i = 0; i < path.Length; i++) {
                char c = path[i];
                if (c < 128 && c != '%') continue;
                if (first == path.Length) first = i;
                if (c < 128) continue;
                if (char.IsSurrogate(c)) {
                    if (!char.IsSurrogatePair(path, i)) return path;
                    extra += 10; i++; // Two UTF-16 units become four escaped bytes.
                } else extra += c < 0x800 ? 5 : 8;
            }
            if (first == path.Length) return path;
            char[] buffer = new char[path.Length + extra];
            for (int i = 0; i < first; i++) buffer[i] = path[i];
            int at = first;
            bool changed = extra != 0;
            for (int i = first; i < path.Length; i++) {
                char c = path[i];
                if (c >= 128) {
                    int scalar = char.ConvertToUtf32(path, i);
                    if (scalar > 0xffff) i++;
                    int shift = scalar < 0x800 ? 6 : scalar < 0x10000 ? 12 : 18;
                    int prefix = shift == 6 ? 0xc0 : shift == 12 ? 0xe0 : 0xf0;
                    at = WriteEscapedByte(buffer, at, prefix | (scalar >> shift));
                    for (shift -= 6; shift >= 0; shift -= 6)
                        at = WriteEscapedByte(buffer, at, 0x80 | ((scalar >> shift) & 63));
                    continue;
                }
                if (c == '%' && i + 2 < path.Length) {
                    int hi = HexDigit(path[i + 1]), lo = HexDigit(path[i + 2]);
                    if (hi >= 0 && lo >= 0) {
                        int octet = hi * 16 + lo;
                        if (octet >= 128) {
                            at = WriteEscapedByte(buffer, at, octet);
                            if (buffer[at - 2] != path[i + 1] || buffer[at - 1] != path[i + 2]) changed = true;
                            i += 2;
                            continue;
                        }
                        if ((octet >= 'a' && octet <= 'z') || (octet >= 'A' && octet <= 'Z') ||
                            (octet >= '0' && octet <= '9') || octet == '-' || octet == '.' || octet == '_' || octet == '~') {
                            c = (char)octet; i += 2; changed = true;
                        }
                    }
                }
                buffer[at++] = c;
            }
            return changed ? new string(buffer, 0, at) : path;
        }

        int WriteEscapedByte(char[] buffer, int at, int value) {
            const string digits = "0123456789ABCDEF";
            buffer[at++] = '%'; buffer[at++] = digits[value >> 4]; buffer[at++] = digits[value & 15];
            return at;
        }

        string NormalizeHttpPort(string hostPort, string defaultPort) {
            int colon = hostPort.LastIndexOf(':');
            if (colon < 0 || colon < hostPort.LastIndexOf(']') || colon + 1 == hostPort.Length) return hostPort;
            int firstDigit = colon + 1;
            for (int i = firstDigit; i < hostPort.Length; i++) {
                char digit = hostPort[i];
                if (digit < '0' || digit > '9') return hostPort;
                if (i == firstDigit && digit == '0' && i + 1 < hostPort.Length) firstDigit++;
            }
            string port = hostPort.Substring(firstDigit);
            if (port == defaultPort) return hostPort.Substring(0, colon);
            return firstDigit == colon + 1 ? hostPort : hostPort.Substring(0, colon + 1) + port;
        }

        char[] suffixChars = { '?', '#' };
        string NormalizeHttpResource(string resource) {
            if (resource.Length == 0 || resource[0] != '/') return "/" + resource;
            int suffix = resource.IndexOfAny(suffixChars);
            string path = suffix < 0 ? resource : resource.Substring(0, suffix);
            string normalized = UnescapePath(path);
            if (normalized == path && path.IndexOf("/.") < 0) return resource;
            path = normalized;
            string tail = suffix < 0 ? "" : resource.Substring(suffix);
            if (path.IndexOf("/.") < 0) return path + tail;
            string[] parts = path.Split('/');
            // Slot zero is the root. Retain empty path segments and the trailing slash of a terminal dot segment.
            int count = 1;
            for (int i = 1; i < parts.Length; i++) {
                string part = parts[i];
                if (part == "." || part == "..") {
                    if (part == ".." && count > 1) count--;
                    if (i + 1 == parts.Length) parts[count++] = "";
                } else {
                    parts[count++] = part;
                }
            }
            return string.Join("/", parts, 0, count) + tail;
        }

        bool PreservesIds(string[] sourceIds, VRCUrl[] sourceUrls, bool compareAliases) {
            if (sourceIds[0] != ids[0]) return false;
            for (int i = 1; i < sourceUrls.Length; i++) {
                // Old lists may contain two spellings of a single endpoint. An exact row keeps its own ID.
                bool foundAlias = false, keptAliasId = false;
                for (int j = 1; j < urls.Length; j++) {
                    if (sourceUrls[i].Equals(urls[j])) {
                        if (sourceIds[i] != ids[j]) return false;
                        foundAlias = false;
                        break;
                    }
                    if (compareAliases && SameSourceEndpoint(sourceUrls[i], urls[j])) {
                        foundAlias = true;
                        if (sourceIds[i] == ids[j]) keptAliasId = true;
                    }
                }
                if (foundAlias && !keptAliasId) return false;
            }
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
