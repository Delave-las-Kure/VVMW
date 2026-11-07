using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

namespace JLChnToZ.VRC.VVMW {
    // Optional catalog arbiter, never a playback clock or a second URL loader.
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    [DisallowMultipleComponent]
    public class SourceMirrorCatalogAuthority : UdonSharpBehaviour {
        public const int NotAuthorityReady = 18, NotLeader = 19, RoleChanged = 20,
            InvalidDraft = 21, Busy = 22, SerializationPending = 23, SerializationFailed = 24,
            CommittedLocal = 25, Confirmed = 26, Unconfirmed = 27, Cancelled = 28,
            WaitingForCatalog = 29, Conflict = 30;
        public Core core;
        public SourceMirrorCatalog catalog;
        public SourceMirrorCatalogAccessPolicy policy;
        public bool enablePublication;

        [UdonSynced] int wireSchema = 1, wireWriter = -1, wireEditor = -1, wireRole;
        [UdonSynced] long wireRevision;
        [UdonSynced] string wireFence = "", wirePlayback = "", wireFilm = "", wireTag = "", wireRequest = "", wireBase = "";
        [UdonSynced] VRCUrl wirePc, wireMobile;
        [UdonSynced] byte wireBackend;
        [UdonSynced] string[] wireIds = new string[0], wireLabels = new string[0];
        [UdonSynced] VRCUrl[] wireUrls = new VRCUrl[0];
        [UdonSynced] int challengeWriter = -1;
        [UdonSynced] string wireChallenge = "";

        bool applying, registered, writerBase, frozen, hasReceipt, hasIncoming;
        string tenure = "", challenge = "", observedChallenge = "", blockedPlayback = "";
        int observedOwner = -1, receiptWriter = -1, receiptEditor = -1, receiptRole;
        string receiptRequest = "", receiptBase = "", receiptFence = "";
        int status = NotAuthorityReady;
        int catalogStatus = WaitingForCatalog;
        bool publicationPending;
        string pendingId = "", pendingVersion = "", lastLocalId = "";
        int pendingWriter = -1, pendingActor = -1;
        float pendingDeadline;
        bool localSerializationPending;
        int lastSerializationByteCount;
        bool lastSerializationSucceeded;
        string localSerializedVersion = "";
        string serializationAttemptVersion = "", serializationAttemptRequest = "";
        bool reconcileQueued;
        int reconcileAttempts;
        int deferredChallengeWriter = -1;
        string deferredChallenge = "";
        float handoffDeadline;

        // A complete received candidate waits here for the independent Core snapshot.
        int rxWriter, rxEditor, rxRole;
        long rxRevision;
        string rxFence, rxPlayback, rxFilm, rxTag, rxRequest, rxBase;
        VRCUrl rxPc, rxMobile;
        byte rxBackend;
        string[] rxIds, rxLabels;
        VRCUrl[] rxUrls;

        int offerTarget = -1, offerWriter = -1;
        string offerNonce = "", offerVersion = "";
        bool offeredBase, handoffAccepted;
        bool handoffCommitted, handoffCommitReceived, awaitingHandoffCommit;
        bool challengeConsumed;

        public bool IsApplyingCatalog => applying;
        public bool PublicationPending => publicationPending;
        public int PublicationStatus => status;
        public int CatalogStatus => catalogStatus;
        public string PendingRequestId => pendingId;
        public string PendingCommittedVersion => pendingVersion;
        public bool HasCoherentWriterBase => writerBase;
        public int LastSerializationByteCount => lastSerializationByteCount;
        public bool LastSerializationSucceeded => lastSerializationSucceeded;
        public bool IsReadyForPublication => enablePublication && GraphReady() && !frozen &&
            MatchesCoreContext(catalog.ContextPlaybackId, catalog.ContextCanonical, catalog.ContextMobileCanonical, catalog.ContextBackend) &&
            (Networking.IsOwner(gameObject) ? WriterReady() : RouteReady());

        bool GraphReady() => enabled && gameObject.activeInHierarchy && Utilities.IsValid(core) &&
            Utilities.IsValid(catalog) && catalog.authority == this && catalog.enabled && catalog.gameObject.activeInHierarchy &&
            Utilities.IsValid(policy) && policy.enabled && policy.gameObject.activeInHierarchy;
        int OwnerId() { var owner = Networking.GetOwner(gameObject); return Utilities.IsValid(owner) ? owner.playerId : -1; }
        bool RouteReady() {
            if (!GraphReady() || !Networking.IsNetworkSettled) return false;
            var expected = policy.GetAuthorityPlayer();
            return Utilities.IsValid(expected) && OwnerId() == expected.playerId;
        }
        bool OwnerAligned() => RouteReady() && Networking.IsOwner(gameObject) && policy.IsAuthoritativeHere();
        bool FreshLaunch() => core.HasSharedPlaybackContextForUfc && !string.IsNullOrEmpty(challenge) &&
            core.SharedPlaybackMarkerForUfc == challenge && core.SharedPlaybackIdForUfc != blockedPlayback;
        bool WriterReady() => enablePublication && OwnerAligned() && !frozen && (writerBase || FreshLaunch());
        public bool MatchesCoreContext(string playbackId, VRCUrl pc, VRCUrl mobile, byte backend) =>
            Utilities.IsValid(core) && core.HasSharedPlaybackContextForUfc && !string.IsNullOrEmpty(playbackId) &&
            core.SharedPlaybackIdForUfc == playbackId && core.ConfiguredPlayerTypeForUfc == backend &&
            SameUrl(core.CanonicalPcUrlForUfc, pc) && SameUrl(core.EffectiveMobileUrlForUfc, mobile);

        void Start() { Attach(); _RefreshCatalogAuthority(); QueueReconcile(); }
        void OnEnable() { Attach(); }
        void OnDisable() {
            if (registered && Utilities.IsValid(core)) core.UnregisterSharedPlaybackObserverForUfc(this);
            registered = false; writerBase = false; challengeConsumed = false;
            challenge = observedChallenge = ""; CancelPending(Unconfirmed);
        }
        void Attach() {
            if (!GraphReady()) return;
            registered = core.RegisterSharedPlaybackObserverForUfc(this);
            if (registered) _OnSharedPlaybackContextForUfc();
        }
        public void _RefreshCatalogAuthority() {
            if (NetworkCalling.InNetworkCall || !GraphReady() || !enablePublication || !Networking.IsNetworkSettled) return;
            var target = policy.GetAuthorityPlayer();
            if (!Utilities.IsValid(target)) return;
            if (!Networking.IsOwner(gameObject)) {
                bool waitingOffer = offerTarget == target.playerId && offerWriter == OwnerId() &&
                    Token(offerNonce, 128) && Time.realtimeSinceStartup <= handoffDeadline;
                if (target.isLocal && !frozen && !handoffAccepted && !waitingOffer) Networking.SetOwner(target, gameObject);
                return;
            }
            if (!OwnerAligned()) return;
            if (handoffCommitted || (awaitingHandoffCommit && Time.realtimeSinceStartup <= handoffDeadline)) return;
            awaitingHandoffCommit = false;
            if (writerBase && !frozen) return;
            if (handoffAccepted && offeredBase && catalog.Version == offerVersion && hasReceipt) {
                PublishRebind(true); return;
            }
            if (string.IsNullOrEmpty(challenge)) {
                writerBase = false; frozen = false; tenure = "";
                blockedPlayback = core.SharedPlaybackIdForUfc;
                challenge = Guid.NewGuid().ToString("N");
                challengeConsumed = false;
                challengeWriter = OwnerId(); wireChallenge = challenge;
                RequestSerialization();
            }
            ApplyChallenge(challenge);
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(CatalogRecoveryChallenge), OwnerId(), challenge);
        }
        void ApplyChallenge(string nonce) {
            if (nonce == observedChallenge || !registered || !Utilities.IsValid(core)) return;
            if (core.SetNextSharedPlaybackMarkerForUfc(this, nonce)) observedChallenge = nonce;
        }
        void ReconcileConsumedChallenge() {
            if (writerBase || !Networking.IsOwner(gameObject) || string.IsNullOrEmpty(challenge)) return;
            if (FreshLaunch()) { challengeConsumed = true; return; }
            if (!challengeConsumed) return;
            // A consumed launch was stopped/replaced before Apply. Prepare a new
            // challenge for the next explicit launch; never rewrite the current header.
            challengeConsumed = false; challenge = ""; reconcileAttempts = 0;
            _RefreshCatalogAuthority(); QueueReconcile();
        }
        [NetworkCallable(maxEventsPerSecond: 1)]
        public void CatalogRecoveryChallenge(int writerId, string nonce) {
            var actor = NetworkCalling.CallingPlayer;
            if (!NetworkCalling.InNetworkCall || !Utilities.IsValid(actor) || actor.playerId != writerId ||
                writerId != OwnerId() || !Token(nonce, 128)) return;
            deferredChallengeWriter = writerId; deferredChallenge = nonce;
            ReconcileChallenge(); QueueReconcile();
        }
        void ReconcileChallenge() {
            if (!RouteReady()) return;
            if (challengeWriter == OwnerId() && Token(wireChallenge, 128)) ApplyChallenge(wireChallenge);
            else if (deferredChallengeWriter == OwnerId() && Token(deferredChallenge, 128)) ApplyChallenge(deferredChallenge);
        }
        void QueueReconcile() {
            if (reconcileQueued || !enabled || reconcileAttempts >= 60) return;
            reconcileQueued = true; SendCustomEventDelayedSeconds(nameof(_ReconcileCatalog), .5f);
        }
        public void _ReconcileCatalog() {
            reconcileQueued = false; reconcileAttempts++;
            if (NetworkCalling.InNetworkCall) return;
            if (!GraphReady()) { QueueReconcile(); return; }
            ReconcileConsumedChallenge();
            ReconcileChallenge();
            if (!hasIncoming) StageWire();
            TryApplyIncoming(); TryAckOffer();
            if (!Networking.IsOwner(gameObject) || (OwnerAligned() && !writerBase && string.IsNullOrEmpty(challenge)))
                _RefreshCatalogAuthority();
            if (!RouteReady() || awaitingHandoffCommit || (hasIncoming && !catalog.IsCurrent)) QueueReconcile();
        }
        public void _OnSharedPlaybackContextForUfc() {
            if (!GraphReady()) return;
            applying = true;
            if (core.HasSharedPlaybackContextForUfc)
                catalog.BindSharedContext(this, core.SharedPlaybackIdForUfc, core.CanonicalPcUrlForUfc,
                    core.EffectiveMobileUrlForUfc, core.ConfiguredPlayerTypeForUfc);
            else catalog.ClearSharedContext(this);
            applying = false;
            TryApplyIncoming();
            ReconcileConsumedChallenge();
            if (writerBase && OwnerAligned() && !frozen) PublishRebind(false);
        }

        public bool RequestPublishCurrentCatalog(int expectedRoleRevision, string expectedCatalogVersion,
            string expectedPlaybackId, string requestId, VRCUrl[] urls, string[] sourceIds, string[] labels, string[] recordMetadata) {
            if (NetworkCalling.InNetworkCall) { status = SourceMirrorCatalog.LocalOnly; return false; }
            if (publicationPending) { status = Busy; return false; }
            if (!enablePublication || !RouteReady() || !registered || !core.HasSharedPlaybackContextForUfc) {
                status = NotAuthorityReady; return false;
            }
            var me = Networking.LocalPlayer;
            if (!Utilities.IsValid(me) || !Token(requestId, 128) || requestId == lastLocalId) { status = InvalidDraft; return false; }
            if (!Envelope(expectedCatalogVersion, expectedPlaybackId, requestId, urls, sourceIds, labels, recordMetadata)) {
                status = InvalidDraft; return false;
            }
            publicationPending = true; pendingId = requestId; lastLocalId = requestId;
            pendingVersion = ""; pendingWriter = OwnerId(); pendingActor = me.playerId;
            pendingDeadline = Time.realtimeSinceStartup + 15f; status = SerializationPending;
            SendCustomEventDelayedSeconds(nameof(_CatalogPublicationTimeout), 15f);
            var copiedUrls = CopyUrls(urls); var copiedIds = CopyStrings(sourceIds);
            var copiedLabels = CopyStrings(labels); var copiedMetadata = CopyStrings(recordMetadata);
            if (Networking.IsOwner(gameObject))
                ProcessPublish(me, false, expectedRoleRevision, expectedCatalogVersion, expectedPlaybackId,
                    requestId, copiedUrls, copiedIds, copiedLabels, copiedMetadata);
            else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(PublishCurrentCatalog), expectedRoleRevision,
                expectedCatalogVersion, expectedPlaybackId, requestId, copiedUrls, copiedIds, copiedLabels, copiedMetadata);
            return true;
        }
        [NetworkCallable(maxEventsPerSecond: 1)]
        public void PublishCurrentCatalog(int expectedRoleRevision, string expectedCatalogVersion,
            string expectedPlaybackId, string requestId, VRCUrl[] urls, string[] sourceIds, string[] labels, string[] recordMetadata) {
            bool network = NetworkCalling.InNetworkCall;
            var actor = network ? NetworkCalling.CallingPlayer : Networking.LocalPlayer;
            ProcessPublish(actor, network, expectedRoleRevision, expectedCatalogVersion, expectedPlaybackId,
                requestId, urls, sourceIds, labels, recordMetadata);
        }
        bool SameCaller(VRCPlayerApi actor, bool network) => network ?
            NetworkCalling.InNetworkCall && NetworkCalling.CallingPlayer == actor :
            !NetworkCalling.InNetworkCall && Networking.LocalPlayer == actor;
        void ProcessPublish(VRCPlayerApi actor, bool network, int role, string expectedVersion,
            string playbackId, string requestId, VRCUrl[] urls, string[] ids, string[] labels, string[] metadata) {
            if (!Utilities.IsValid(actor)) return;
            // Freeze the bounded caller draft before any world-policy callback.
            if (!Envelope(expectedVersion, playbackId, requestId, urls, ids, labels, metadata)) {
                Reply(actor, requestId, InvalidDraft, ""); return;
            }
            urls = CopyUrls(urls); ids = CopyStrings(ids); labels = CopyStrings(labels); metadata = CopyStrings(metadata);
            if (!WriterReady()) { Reply(actor, requestId, NotAuthorityReady, ""); return; }
            var capturedPolicy = policy; var capturedCore = core; var capturedCatalog = catalog;
            int owner = OwnerId(); string capturedChallenge = challenge, capturedTenure = tenure;
            bool capturedWriterBase = writerBase;
            if (role != policy.GetRoleRevision()) { Reply(actor, requestId, RoleChanged, ""); return; }
            if (!policy.CanPublishCatalog(actor, role)) { Reply(actor, requestId, NotLeader, ""); return; }
            if (hasReceipt && receiptEditor == actor.playerId && receiptRequest == requestId) {
                bool duplicate = receiptBase == expectedVersion && catalog.SnapshotPlaybackId == playbackId &&
                    SameDraft(metadata, ids, labels, urls);
                Reply(actor, requestId, duplicate ? CommittedLocal : InvalidDraft, duplicate ? catalog.Version : ""); return;
            }
            if (catalog.Version != expectedVersion) { Reply(actor, requestId, SourceMirrorCatalog.CatalogChanged, ""); return; }
            if (core.SharedPlaybackIdForUfc != playbackId) { Reply(actor, requestId, SourceMirrorCatalog.PlaybackChanged, ""); return; }
            VRCUrl pc = core.CanonicalPcUrlForUfc, mobile = core.EffectiveMobileUrlForUfc;
            byte backend = core.ConfiguredPlayerTypeForUfc;
            // Clone before callbacks and revalidate the actual actor/role/header after them.
            var copyUrls = CopyUrls(urls); var copyIds = CopyStrings(ids); var copyLabels = CopyStrings(labels);
            string film = metadata[0], tag = metadata[1];
            applying = true;
            bool valid = catalog.ValidateSharedDraft(this, expectedVersion, playbackId, film, tag, copyIds, copyLabels, copyUrls);
            applying = false;
            if (!valid) { Reply(actor, requestId, catalog.Result, ""); return; }
            if (policy != capturedPolicy || core != capturedCore || catalog != capturedCatalog || OwnerId() != owner ||
                challenge != capturedChallenge || !WriterReady() || !SameCaller(actor, network) ||
                policy.GetRoleRevision() != role || !policy.CanPublishCatalog(actor, role) ||
                !MatchesCoreContext(playbackId, pc, mobile, backend) || catalog.Version != expectedVersion ||
                !SameCaller(actor, network) || OwnerId() != owner || policy.GetRoleRevision() != role || !policy.IsAuthoritativeHere() ||
                policy != capturedPolicy || core != capturedCore || catalog != capturedCatalog ||
                !MatchesCoreContext(playbackId, pc, mobile, backend) || catalog.Version != expectedVersion ||
                !GraphReady() || !enablePublication || frozen || challenge != capturedChallenge ||
                tenure != capturedTenure || writerBase != capturedWriterBase || !SameCaller(actor, network) ||
                OwnerId() != owner || (!writerBase && !FreshLaunch())) {
                Reply(actor, requestId, RoleChanged, ""); return;
            }
            bool newEpoch = !writerBase;
            string nextTenure = newEpoch || string.IsNullOrEmpty(tenure) ? Guid.NewGuid().ToString("N") : tenure;
            long next = newEpoch ? 1 : catalog.Revision + 1;
            if (!newEpoch && catalog.Revision == long.MaxValue) { Reply(actor, requestId, SourceMirrorCatalog.RevisionExhausted, ""); return; }
            int candidateResult = ValidateWireCandidate(owner, nextTenure, next, playbackId, pc, mobile, backend,
                film, tag, copyIds, copyLabels, copyUrls, requestId, expectedVersion);
            if (candidateResult != SourceMirrorCatalog.Accepted) { Reply(actor, requestId, candidateResult, ""); return; }
            bool previousSerializationPending = localSerializationPending;
            string previousSerializedVersion = localSerializedVersion;
            localSerializationPending = true; localSerializedVersion = nextTenure + ":" + next;
            if (!TryApplyCandidate(owner, nextTenure, next, playbackId, pc, mobile, backend, film, tag,
                copyIds, copyLabels, copyUrls, actor.playerId, role, requestId, expectedVersion, true)) {
                localSerializationPending = previousSerializationPending; localSerializedVersion = previousSerializedVersion;
                Reply(actor, requestId, Conflict, ""); return;
            }
            SetWire(nextTenure, next, playbackId, pc, mobile, backend, film, tag,
                copyIds, copyLabels, copyUrls, actor.playerId, role, requestId, expectedVersion);
            StageWire();
            writerBase = true;
            status = SerializationPending; RequestSerialization();
            Reply(actor, requestId, CommittedLocal, catalog.Version);
        }
        bool SameDraft(string[] metadata, string[] ids, string[] labels, VRCUrl[] urls) =>
            metadata[0] == catalog.FilmId && metadata[1] == catalog.CompatibilityTag &&
            SameStrings(ids, catalog.GetSourceIds()) && SameStrings(labels, catalog.GetSourceLabels()) && SameUrls(urls, catalog.GetSourceUrls());

        void SetWire(string fence, long revision, string playback, VRCUrl pc, VRCUrl mobile, byte backend,
            string film, string tag, string[] ids, string[] labels, VRCUrl[] urls, int editor, int role, string request, string previous) {
            wireSchema = 1; wireWriter = OwnerId(); wireFence = fence; wireRevision = revision;
            wirePlayback = playback; wirePc = pc; wireMobile = mobile; wireBackend = backend;
            wireFilm = film; wireTag = tag; wireIds = CopyStrings(ids); wireLabels = CopyStrings(labels); wireUrls = CopyUrls(urls);
            wireEditor = editor; wireRole = role; wireRequest = request; wireBase = previous;
        }
        // Validate the complete candidate before touching live synced or staged fields.
        int ValidateWireCandidate(int writer, string fence, long revision, string playback, VRCUrl pc, VRCUrl mobile,
            byte backend, string film, string tag, string[] ids, string[] labels, VRCUrl[] urls, string request, string previous) {
            if (!GraphReady() || writer < 0 || (OwnerId() >= 0 && writer != OwnerId()) ||
                !Token(fence, 96) || revision <= 0 || !Token(playback, 128) ||
                VRCUrl.IsNullOrEmpty(pc) || VRCUrl.IsNullOrEmpty(mobile) || backend == 0 ||
                !Envelope(previous, playback, string.IsNullOrEmpty(request) ? "rebind" : request,
                    urls, ids, labels, new string[] { film, tag }) || !SameUrl(pc, urls[0])) return InvalidDraft;
            if (Bytes(pc.Get()) + Bytes(mobile.Get()) + PayloadBytes(urls, ids, labels) +
                Bytes(film) + Bytes(tag) + 1024 > SourceMirrorCatalog.SnapshotBudget) return SourceMirrorCatalog.PayloadTooLarge;
            if (hasReceipt && fence == receiptFence && revision < catalog.Revision) return Conflict;
            if (hasIncoming && rxWriter == writer && rxFence == fence && revision < rxRevision) return Conflict;
            return SourceMirrorCatalog.Accepted;
        }
        bool StageWire() {
            if (wireSchema != 1 || ValidateWireCandidate(wireWriter, wireFence, wireRevision, wirePlayback,
                wirePc, wireMobile, wireBackend, wireFilm, wireTag, wireIds, wireLabels, wireUrls,
                wireRequest, wireBase) != SourceMirrorCatalog.Accepted) return false;
            rxWriter = wireWriter; rxEditor = wireEditor; rxRole = wireRole; rxRevision = wireRevision;
            rxFence = wireFence; rxPlayback = wirePlayback; rxPc = wirePc; rxMobile = wireMobile; rxBackend = wireBackend;
            rxFilm = wireFilm; rxTag = wireTag; rxRequest = wireRequest; rxBase = wireBase;
            rxIds = CopyStrings(wireIds); rxLabels = CopyStrings(wireLabels); rxUrls = CopyUrls(wireUrls);
            hasIncoming = true; return true;
        }
        bool TryApplyIncoming() {
            if (!hasIncoming) { catalogStatus = WaitingForCatalog; return false; }
            return TryApplyCandidate(rxWriter, rxFence, rxRevision, rxPlayback, rxPc, rxMobile, rxBackend,
                rxFilm, rxTag, rxIds, rxLabels, rxUrls, rxEditor, rxRole, rxRequest, rxBase, false);
        }
        bool TryApplyCandidate(int writer, string fence, long revision, string playback, VRCUrl pc, VRCUrl mobile,
            byte backend, string film, string tag, string[] ids, string[] labels, VRCUrl[] urls,
            int editor, int role, string request, string previous, bool verifiedLocalPublication) {
            // Only ProcessPublish may reuse its final authorization guard. No world-policy
            // callback may run between that guard and publishing the accepted local data.
            if ((!verifiedLocalPublication && !RouteReady() && !FrozenBaseReadAllowed(writer, fence, revision)) ||
                !GraphReady() || writer != OwnerId() ||
                !MatchesCoreContext(playback, pc, mobile, backend)) { catalogStatus = WaitingForCatalog; return false; }
            string incomingVersion = fence + ":" + revision;
            if (hasReceipt && incomingVersion == catalog.Version &&
                (editor != receiptEditor || role != receiptRole || request != receiptRequest || previous != receiptBase)) {
                catalogStatus = Conflict; return false;
            }
            applying = true;
            bool accepted = catalog.AcceptSharedSnapshot(this, fence, revision, playback,
                film, tag, ids, labels, urls, !hasReceipt || receiptFence != fence);
            applying = false;
            if (!accepted) { catalogStatus = Conflict; return false; }
            catalogStatus = SourceMirrorCatalog.Accepted;
            hasReceipt = true; tenure = fence; receiptFence = fence; receiptWriter = writer;
            receiptEditor = editor; receiptRole = role; receiptRequest = request; receiptBase = previous;
            ConfirmPending(); TryAckOffer(); return true;
        }
        bool FrozenBaseReadAllowed(int writer, string fence, long revision) {
            var me = Networking.LocalPlayer;
            return hasReceipt && Utilities.IsValid(me) && me.playerId == offerTarget &&
                offerWriter == OwnerId() && receiptWriter == offerWriter && writer == offerWriter &&
                fence == receiptFence && revision >= catalog.Revision &&
                fence + ":" + revision == offerVersion && Token(offerNonce, 128) &&
                Time.realtimeSinceStartup <= handoffDeadline;
        }
        public override void OnDeserialization() {
            reconcileAttempts = 0; ReconcileChallenge();
            if (StageWire()) TryApplyIncoming(); QueueReconcile();
        }
        void PublishRebind(bool handoff) {
            if (!hasReceipt || !core.HasSharedPlaybackContextForUfc || !OwnerAligned() || frozen ||
                !SameUrl(catalog.SnapshotCanonical, core.CanonicalPcUrlForUfc) ||
                !SameUrl(catalog.SnapshotMobileCanonical, core.EffectiveMobileUrlForUfc) ||
                catalog.SnapshotBackend != core.ConfiguredPlayerTypeForUfc || catalog.Revision == long.MaxValue ||
                (!handoff && catalog.SnapshotPlaybackId == core.SharedPlaybackIdForUfc)) return;
            int owner = OwnerId(), editor = receiptEditor, role = receiptRole;
            string nextTenure = handoff ? Guid.NewGuid().ToString("N") : tenure;
            long next = catalog.Revision + 1;
            string playback = core.SharedPlaybackIdForUfc, film = catalog.FilmId, tag = catalog.CompatibilityTag, previous = catalog.Version;
            VRCUrl pc = core.CanonicalPcUrlForUfc, mobile = core.EffectiveMobileUrlForUfc;
            byte backend = core.ConfiguredPlayerTypeForUfc;
            var ids = catalog.GetSourceIds(); var labels = catalog.GetSourceLabels(); var urls = catalog.GetSourceUrls();
            if (ValidateWireCandidate(owner, nextTenure, next, playback, pc, mobile, backend, film, tag,
                ids, labels, urls, "", previous) != SourceMirrorCatalog.Accepted ||
                !TryApplyCandidate(owner, nextTenure, next, playback, pc, mobile, backend, film, tag,
                    ids, labels, urls, editor, role, "", previous, false)) return;
            SetWire(nextTenure, next, playback, pc, mobile, backend, film, tag, ids, labels, urls, editor, role, "", previous);
            StageWire(); writerBase = true; frozen = false; RequestSerialization();
        }

        void Reply(VRCPlayerApi actor, string requestId, int code, string committedVersion) {
            if (!Token(requestId, 128)) return;
            if (actor.isLocal) AcceptReply(actor.playerId, requestId, code, committedVersion);
            else SendCustomNetworkEvent(NetworkEventTarget.All, nameof(CatalogPublishResult), actor.playerId, requestId, code, committedVersion);
        }
        [NetworkCallable(maxEventsPerSecond: 4)]
        public void CatalogPublishResult(int recipientPlayerId, string requestId, int code, string committedVersion) {
            var sender = NetworkCalling.CallingPlayer;
            if (!NetworkCalling.InNetworkCall || !Utilities.IsValid(sender) || sender.playerId != OwnerId() ||
                sender.playerId != pendingWriter || !RouteReady()) return;
            AcceptReply(recipientPlayerId, requestId, code, committedVersion);
        }
        void AcceptReply(int recipientPlayerId, string requestId, int code, string committedVersion) {
            var me = Networking.LocalPlayer;
            if (!publicationPending || !Utilities.IsValid(me) || recipientPlayerId != me.playerId ||
                pendingActor != me.playerId || requestId != pendingId || pendingWriter != OwnerId()) return;
            status = code;
            if (code == CommittedLocal || code == SerializationPending) { pendingVersion = committedVersion; ConfirmPending(); }
            else if (code != Confirmed) CancelPending(code);
        }
        void ConfirmPending() {
            if (!publicationPending || !hasReceipt || pendingWriter != OwnerId() || receiptWriter != pendingWriter ||
                receiptEditor != pendingActor || receiptRequest != pendingId || !catalog.IsCurrent ||
                (!string.IsNullOrEmpty(pendingVersion) && pendingVersion != catalog.Version)) return;
            if (Networking.IsOwner(gameObject) && localSerializationPending) return;
            pendingVersion = catalog.Version; publicationPending = false; status = Confirmed;
        }
        void CancelPending(int code) { if (publicationPending) { publicationPending = false; status = code; } }
        public void _CatalogPublicationTimeout() {
            if (!publicationPending) return;
            float remaining = pendingDeadline - Time.realtimeSinceStartup;
            if (remaining > 0f) { SendCustomEventDelayedSeconds(nameof(_CatalogPublicationTimeout), remaining + .01f); return; }
            CancelPending(Unconfirmed);
        }
        public override void OnPreSerialization() {
            serializationAttemptVersion = wireFence + ":" + wireRevision;
            serializationAttemptRequest = wireRequest;
        }
        public override void OnPostSerialization(SerializationResult result) {
            lastSerializationByteCount = result.byteCount;
            lastSerializationSucceeded = result.success;
            if (!localSerializationPending || serializationAttemptVersion != localSerializedVersion ||
                serializationAttemptRequest != wireRequest) return;
            localSerializationPending = false;
            if (!result.success) { CancelPending(SerializationFailed); status = SerializationFailed; return; }
            if (localSerializedVersion == catalog.Version) ConfirmPending();
        }

        public bool RequestWriterHandoff(VRCPlayerApi target) {
            if (NetworkCalling.InNetworkCall || !GraphReady() || !enablePublication || !Networking.IsOwner(gameObject) ||
                !writerBase || !hasReceipt || frozen || !Utilities.IsValid(target) || target.isLocal || publicationPending) return false;
            frozen = true; offerTarget = target.playerId; offerWriter = OwnerId();
            handoffCommitted = handoffCommitReceived = false;
            offerNonce = Guid.NewGuid().ToString("N"); offerVersion = catalog.Version;
            handoffDeadline = Time.realtimeSinceStartup + 15f;
            SendCustomEventDelayedSeconds(nameof(_WriterHandoffTimeout), 15f);
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(OfferWriterHandoff), offerTarget, offerNonce, offerVersion);
            return true;
        }
        [NetworkCallable(maxEventsPerSecond: 1)]
        public void OfferWriterHandoff(int targetPlayerId, string nonce, string version) {
            var sender = NetworkCalling.CallingPlayer; var me = Networking.LocalPlayer;
            if (!NetworkCalling.InNetworkCall || !Utilities.IsValid(sender) || !Utilities.IsValid(me) ||
                sender.playerId != OwnerId() || targetPlayerId != me.playerId ||
                !Token(nonce, 128) || !Token(version, 128)) return;
            offerWriter = sender.playerId; offerTarget = targetPlayerId; offerNonce = nonce; offerVersion = version;
            offeredBase = false; handoffCommitReceived = false; handoffDeadline = Time.realtimeSinceStartup + 15f;
            TryAckOffer(); QueueReconcile();
        }
        void TryAckOffer() {
            var me = Networking.LocalPlayer;
            if (offeredBase || !Utilities.IsValid(me) || me.playerId != offerTarget ||
                offerWriter != OwnerId() || !hasReceipt || receiptWriter != offerWriter ||
                catalog.Version != offerVersion || Time.realtimeSinceStartup > handoffDeadline) return;
            var expected = Utilities.IsValid(policy) ? policy.GetAuthorityPlayer() : null;
            if (!Utilities.IsValid(expected) || (expected.playerId != offerWriter && expected.playerId != offerTarget)) return;
            offeredBase = true;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(AckWriterHandoff), offerWriter, offerNonce, offerVersion);
        }
        public void _WriterHandoffTimeout() {
            if (!frozen || OwnerId() != offerWriter) return;
            float remaining = handoffDeadline - Time.realtimeSinceStartup;
            if (remaining > 0f) { SendCustomEventDelayedSeconds(nameof(_WriterHandoffTimeout), remaining + .01f); return; }
            if (handoffCommitted) { status = Unconfirmed; return; } // A committed freeze cannot silently resume writes.
            frozen = false; offerTarget = -1; offerNonce = offerVersion = ""; status = Unconfirmed;
        }
        [NetworkCallable(maxEventsPerSecond: 1)]
        public void AckWriterHandoff(int recipientPlayerId, string nonce, string version) {
            var sender = NetworkCalling.CallingPlayer;
            if (!NetworkCalling.InNetworkCall || !Utilities.IsValid(sender) || !frozen || !Networking.IsOwner(gameObject) ||
                recipientPlayerId != OwnerId() || sender.playerId != offerTarget || nonce != offerNonce ||
                version != offerVersion || catalog.Version != offerVersion) return;
            handoffCommitted = true;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(CommitWriterHandoff), offerTarget, nonce, version);
            Networking.SetOwner(sender, gameObject);
        }
        [NetworkCallable(maxEventsPerSecond: 1)]
        public void CommitWriterHandoff(int targetPlayerId, string nonce, string version) {
            var sender = NetworkCalling.CallingPlayer; var me = Networking.LocalPlayer;
            if (!NetworkCalling.InNetworkCall || !Utilities.IsValid(sender) || !Utilities.IsValid(me) ||
                sender.playerId != offerWriter || targetPlayerId != offerTarget || me.playerId != offerTarget ||
                nonce != offerNonce || version != offerVersion || !offeredBase || !hasReceipt ||
                catalog.Version != offerVersion || Time.realtimeSinceStartup > handoffDeadline ||
                (OwnerId() != offerWriter && OwnerId() != offerTarget)) return;
            handoffCommitReceived = true;
            if (Networking.IsOwner(gameObject)) {
                handoffAccepted = true; awaitingHandoffCommit = false;
                SendCustomEventDelayedFrames(nameof(_RefreshCatalogAuthority), 1);
            }
        }
        public override bool OnOwnershipRequest(VRCPlayerApi requester, VRCPlayerApi newOwner) {
            if (!Utilities.IsValid(newOwner)) return false;
            var expected = Utilities.IsValid(policy) ? policy.GetAuthorityPlayer() : null;
            return (Utilities.IsValid(expected) && expected == newOwner) ||
                (Utilities.IsValid(requester) && (requester.playerId == offerWriter || requester.playerId == offerTarget) &&
                    newOwner.playerId == offerTarget && Token(offerNonce, 128) &&
                    catalog.Version == offerVersion && Time.realtimeSinceStartup <= handoffDeadline && (frozen || offeredBase));
        }
        public override void OnOwnershipTransferred(VRCPlayerApi newOwner) {
            CancelPending(Unconfirmed); localSerializationPending = false;
            int nextOwner = Utilities.IsValid(newOwner) ? newOwner.playerId : -1;
            bool cooperativeDeparture = frozen && nextOwner == offerTarget && catalog.Version == offerVersion &&
                Time.realtimeSinceStartup <= handoffDeadline;
            if (!cooperativeDeparture) handoffCommitted = false;
            handoffAccepted = Utilities.IsValid(newOwner) && newOwner.isLocal && offeredBase && handoffCommitReceived &&
                nextOwner == offerTarget && catalog.Version == offerVersion && hasReceipt &&
                Time.realtimeSinceStartup <= handoffDeadline;
            awaitingHandoffCommit = Utilities.IsValid(newOwner) && newOwner.isLocal && offeredBase && !handoffAccepted &&
                nextOwner == offerTarget && Time.realtimeSinceStartup <= handoffDeadline;
            writerBase = false; challengeConsumed = false;
            challenge = ""; observedChallenge = ""; observedOwner = nextOwner;
            if (!handoffAccepted) { tenure = ""; frozen = cooperativeDeparture; if (!awaitingHandoffCommit) offeredBase = false; }
            SendCustomEventDelayedFrames(nameof(_RefreshCatalogAuthority), 2);
            reconcileAttempts = 0; QueueReconcile();
        }
        public override void OnMasterTransferred(VRCPlayerApi newMaster) {
            reconcileAttempts = 0; QueueReconcile(); SendCustomEventDelayedFrames(nameof(_RefreshCatalogAuthority), 3);
        }
        public override void OnPlayerJoined(VRCPlayerApi player) {
            reconcileAttempts = 0; QueueReconcile();
            if (enablePublication && Networking.IsOwner(gameObject) && GraphReady()) RequestSerialization();
        }

        bool Envelope(string version, string playback, string request, VRCUrl[] urls, string[] ids, string[] labels, string[] metadata) {
            if (version == null || version.Length > 128 || !Token(playback, 128) || !Token(request, 128) ||
                metadata == null || metadata.Length != 2 || string.IsNullOrEmpty(metadata[0]) || metadata[0].Length > 64 ||
                !Id(metadata[0]) || string.IsNullOrEmpty(metadata[1]) || metadata[1].Length > 128 || urls == null || ids == null || labels == null ||
                urls.Length < 1 || urls.Length > 3 || ids.Length != urls.Length || labels.Length != urls.Length) return false;
            for (int i = 0; i < urls.Length; i++) {
                if (VRCUrl.IsNullOrEmpty(urls[i]) || !Id(ids[i]) || labels[i] == null || labels[i].Length > 128) return false;
                for (int j = 0; j < i; j++) if (ids[i] == ids[j] || SameUrl(urls[i], urls[j])) return false;
            }
            return PayloadBytes(urls, ids, labels) + Bytes(version) + Bytes(playback) + Bytes(request) +
                Bytes(metadata[0]) + Bytes(metadata[1]) + 512 <= SourceMirrorCatalog.DraftBudget;
        }
        int PayloadBytes(VRCUrl[] urls, string[] ids, string[] labels) {
            int bytes = 0; for (int i = 0; i < urls.Length; i++) bytes += Bytes(urls[i].Get()) + Bytes(ids[i]) + Bytes(labels[i]) + 12; return bytes;
        }
        int Bytes(string value) {
            if (value == null) return 0;
            if (value.Length > SourceMirrorCatalog.SnapshotBudget) return SourceMirrorCatalog.SnapshotBudget + 1;
            int bytes = 0;
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (c <= 127) bytes++;
                else if (c <= 2047) bytes += 2;
                else if (c >= 0xD800 && c <= 0xDBFF && i + 1 < value.Length && value[i + 1] >= 0xDC00 && value[i + 1] <= 0xDFFF) { bytes += 4; i++; }
                else bytes += 3;
                if (bytes > SourceMirrorCatalog.SnapshotBudget) return SourceMirrorCatalog.SnapshotBudget + 1;
            }
            return bytes;
        }
        bool Token(string value, int limit) {
            if (string.IsNullOrEmpty(value) || value.Length > limit) return false;
            for (int i = 0; i < value.Length; i++) if (value[i] < 32 || value[i] == 127) return false;
            return true;
        }
        bool Id(string value) {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')) return false;
            }
            return true;
        }
        bool SameUrl(VRCUrl a, VRCUrl b) => !VRCUrl.IsNullOrEmpty(a) && !VRCUrl.IsNullOrEmpty(b) && a.Equals(b);
        bool SameStrings(string[] a, string[] b) {
            if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true;
        }
        bool SameUrls(VRCUrl[] a, VRCUrl[] b) {
            if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (!SameUrl(a[i], b[i])) return false; return true;
        }
        string[] CopyStrings(string[] source) { var copy = new string[source.Length]; for (int i = 0; i < source.Length; i++) copy[i] = source[i]; return copy; }
        VRCUrl[] CopyUrls(VRCUrl[] source) { var copy = new VRCUrl[source.Length]; for (int i = 0; i < source.Length; i++) copy[i] = source[i]; return copy; }
    }
}
