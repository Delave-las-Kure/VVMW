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
        public const int WaitingForFirstMarker = 1, RelaunchForFirstCatalog = 2;
        public Core core;
        public SourceMirrorCatalog catalog;
        public SourceMirrorCatalogAccessPolicy policy;
        public bool enablePublication;
        // Opt-in protocol diagnostics only; never changes routing, timers or media.
        public bool traceProtocol;
        int protocolSequence;

        void TraceProtocol(string kind, string playback, string version, string request, int peer, int code, int recipient = -1) {
            if (!traceProtocol) return;
            var me = Networking.LocalPlayer;
            Debug.Log("[VVMW CATALOG PROTOCOL] seq=" + (++protocolSequence) +
                " t=" + Time.realtimeSinceStartup.ToString("F3") +
                " event=" + kind + " actor=" + (Utilities.IsValid(me) ? me.playerId : -1) +
                " writer=" + OwnerId() + " peer=" + peer + " recipient=" + recipient + " code=" + code +
                " packetPlayback=" + playback + " packetVersion=" + version + " request=" + request +
                " core=" + (Utilities.IsValid(core) ? core.SharedPlaybackIdForUfc : "") +
                " accepted=" + (Utilities.IsValid(catalog) ? catalog.Version : "") +
                " current=" + (Utilities.IsValid(catalog) && catalog.IsCurrent) +
                " pending=" + publicationPending + " pendingId=" + pendingId +
                " pendingVersion=" + pendingVersion + " status=" + status + " catalogStatus=" + catalogStatus);
        }

        [UdonSynced] int wireSchema = 1, wireWriter = -1, wireEditor = -1, wireRole;
        [UdonSynced] long wireRevision, wireSequence;
        long localSequence, recoverySequence;
        [UdonSynced] string wireFence = "", wirePlayback = "", wireFilm = "", wireTag = "", wireRequest = "", wireBase = "";
        [UdonSynced] VRCUrl wirePc, wireMobile;
        [UdonSynced] byte wireBackend;
        [UdonSynced] string[] wireIds = new string[0], wireLabels = new string[0];
        [UdonSynced] VRCUrl[] wireUrls = new VRCUrl[0];
        [UdonSynced] int challengeWriter = -1;
        [UdonSynced] string wireChallenge = "";
        // A retained list offered to the editor after abrupt ownership loss. It
        // describes the OLD playback and is never staged as a playable snapshot.
        [UdonSynced] string wireRecoveryPlayback = "";
        // Different owners start at revision 1. Advertise recent predecessor
        // epochs so their revision numbers never have to be compared directly.
        const int MaxPreviousFences = 4;
        [UdonSynced] string[] wirePreviousFences = new string[0];

        bool applying, registered, writerBase, frozen, hasReceipt, hasIncoming;
        string tenure = "", challenge = "", observedChallenge = "", blockedPlayback = "";
        int observedOwner = -1, receiptWriter = -1, receiptEditor = -1, receiptRole;
        string receiptRequest = "", receiptBase = "", receiptFence = "";
        string[] receiptPreviousFences = new string[0], recoveryPreviousFences = new string[0];
        int status = NotAuthorityReady;
        int catalogStatus = WaitingForCatalog;
        bool publicationPending, awaitingLateReceipt;
        // Superseded attempts can still receive an exact snapshot or a writer reply. The two cinema
        // remotes keep separate receipts; a draft shares evidence of its accepted version across retries.
        const int InitialLateCapacity = 8;
        string[][] lateTargets = new string[InitialLateCapacity][];
        string[][] lateDrafts = new string[InitialLateCapacity][];
        UdonSharpBehaviour[] lateViews = new UdonSharpBehaviour[InitialLateCapacity];
        string[] lateRequests = new string[InitialLateCapacity], lateBases = new string[InitialLateCapacity],
            latePlaybacks = new string[InitialLateCapacity], lateVersions = new string[InitialLateCapacity];
        int[] lateWriters = new int[InitialLateCapacity], lateRoles = new int[InitialLateCapacity], lateStops = new int[InitialLateCapacity];
        bool[] lateExact = new bool[InitialLateCapacity];
        int nextLateSlot;
        UdonSharpBehaviour pendingView;
        string rejectedLocalRequest = "";
        string[] pendingConfirmation, pendingDraft;
        string confirmedLocalRequest = "";
        string pendingFirstMarker = "", consumedFirstMarker = "";
        string lastUnmarkedPlayback = "", carryFromPlayback = "", carryToPlayback = "";
        int carryRole;
        VRCUrl pendingPc, pendingMobile;
        byte pendingBackend;
        int pendingStopRevision;
        string pendingId = "", pendingVersion = "", lastLocalId = "", pendingBase = "", pendingPlayback = "";
        int pendingWriter = -1, pendingActor = -1, pendingRole;
        float pendingDeadline;
        bool localSerializationPending;
        int lastSerializationByteCount;
        bool lastSerializationSucceeded;
        string localSerializedVersion = "";
        string serializationAttemptVersion = "", serializationAttemptRequest = "";
        string serializationAttemptChallenge = "", serializationAttemptRecovery = "";
        int serializationRetries, serializationStopRevision;
        bool retryAwaitingHeader;
        bool retryQueued, rebindRetryQueued, serializingChallenge, contextPublicationDeferred;
        bool reconcileQueued, recoveryCaptureDeferred;
        int reconcileAttempts;
        int deferredChallengeWriter = -1;
        string deferredChallenge = "";
        int nextChallengeGeneration, deferredGeneration;
        int recoveryWriter = -1;
        string recoveryPlayback = "", recoveryMarker = "", recoveryVersion = "", recoveryFence = "", recoveryFilm = "", recoveryTag = "";
        long recoveryRevision;
        VRCUrl recoveryPc, recoveryMobile;
        byte recoveryBackend;
        string[] recoveryIds = new string[0], recoveryLabels = new string[0];
        VRCUrl[] recoveryUrls = new VRCUrl[0];
        float handoffDeadline;

        // Ownership and Core headers are delivered independently of the catalog.
        // Keep each writer's validated snapshot until the local owner and playback headers catch up.
        string[] rxRecoveryPlayback = new string[2], rxRecoveryMarker = new string[2];
        int[] rxWriter = { -1, -1 }, rxEditor = new int[2], rxRole = new int[2];
        long[] rxRevision = new long[2], rxSequence = new long[2];
        string[] rxFence = new string[2], rxPlayback = new string[2], rxFilm = new string[2],
            rxTag = new string[2], rxRequest = new string[2], rxBase = new string[2];
        VRCUrl[] rxPc = new VRCUrl[2], rxMobile = new VRCUrl[2];
        byte[] rxBackend = new byte[2];
        string[][] rxIds = new string[2][], rxLabels = new string[2][], rxPreviousFences = new string[2][];
        VRCUrl[][] rxUrls = new VRCUrl[2][];

        int offerTarget = -1, offerWriter = -1;
        string offerNonce = "", offerVersion = "";
        bool offeredBase, handoffAccepted;
        bool handoffCommitted, handoffCommitReceived, awaitingHandoffCommit;
        bool challengeConsumed;
        string firstPlayback = "", firstMarker;
        VRCUrl firstPc, firstMobile;
        byte firstBackend;
        int firstStopRevision;

        public bool IsApplyingCatalog => applying;
        public bool PublicationPending => publicationPending;
        public int PublicationStatus => status;
        public int CatalogStatus => catalogStatus;
        public string PendingRequestId => pendingId;
        public string PendingCommittedVersion => pendingVersion;
        public bool HasCoherentWriterBase => writerBase;
        // On a remote editor only the current writer can diagnose a missing first
        // marker. Its authenticated denial identifies exactly which marker is needed.
        public int FirstCatalogLaunchHint {
            get {
                if (!enablePublication || !registered || !RouteReady() || frozen || hasReceipt ||
                    catalog.HasAcceptedSnapshot || !core.HasSharedPlaybackContextForUfc ||
                    !string.IsNullOrEmpty(core.SharedPlaybackMarkerForUfc)) return 0;
                string marker;
                if (Networking.IsOwner(gameObject)) {
                    if (!WriterNeedsFirstMarker()) return 0;
                    marker = challenge;
                } else {
                    if (status != NotAuthorityReady || pendingWriter != OwnerId() ||
                        pendingRole != policy.GetRoleRevision() || pendingPlayback != core.SharedPlaybackIdForUfc ||
                        string.IsNullOrEmpty(pendingFirstMarker)) return 0;
                    marker = pendingFirstMarker;
                }
                if (marker == consumedFirstMarker) return 0;
                if (observedChallenge == marker) return RelaunchForFirstCatalog;
                // A previously observed different marker may already supersede the reply.
                return string.IsNullOrEmpty(observedChallenge) ? WaitingForFirstMarker : 0;
            }
        }
        public bool CanCarryFirstCatalogDraft(string previousPlayback, int previousRole) =>
            enablePublication && registered && RouteReady() && !frozen && !hasReceipt && !catalog.HasAcceptedSnapshot &&
            !string.IsNullOrEmpty(carryToPlayback) && core.HasSharedPlaybackContextForUfc &&
            core.SharedPlaybackIdForUfc == carryToPlayback && previousPlayback == carryFromPlayback &&
            previousRole == carryRole && carryRole == policy.GetRoleRevision();

        public bool RequiresPublicationRetry => localSerializationPending && Networking.IsOwner(gameObject) &&
            Utilities.IsValid(catalog) && catalog.IsCurrent && localSerializedVersion == catalog.Version;
        public int LastSerializationByteCount => lastSerializationByteCount;
        public bool LastSerializationSucceeded => lastSerializationSucceeded;
        public string RecoveryBaseVersion => GraphReady() && !catalog.IsCurrent &&
            recoveryWriter == OwnerId() && (recoveryRevision == 0 || core.SharedPlaybackMarkerForUfc == recoveryMarker) &&
            MatchesCoreContext(recoveryPlayback, recoveryPc, recoveryMobile, recoveryBackend) &&
            (recoveryRevision > 0 || HasLocalRecoveryRows()) ? recoveryVersion : "";
        // A peer may have received a later snapshot than the replacement writer.
        // Keep those rows for the host to review. The compare-and-swap still uses
        // the exact retained version advertised by the replacement writer.
        bool HasLocalRecoveryRows() => Utilities.IsValid(catalog) && catalog.HasAcceptedSnapshot &&
            SameUrl(catalog.SnapshotCanonical, recoveryPc) && SameUrl(catalog.SnapshotMobileCanonical, recoveryMobile) &&
            catalog.SnapshotBackend == recoveryBackend;
        bool HasNewerRecoveryRows() => HasLocalRecoveryRows() && (recoveryRevision == 0 ||
            (receiptFence == recoveryFence ? catalog.Revision > recoveryRevision :
                ContainsFence(receiptPreviousFences, recoveryFence) && !ContainsFence(recoveryPreviousFences, receiptFence)));
        public bool HasRecoveryConflict => !string.IsNullOrEmpty(RecoveryBaseVersion) &&
            recoveryRevision > 0 && HasLocalRecoveryRows() && receiptFence != recoveryFence &&
            ContainsFence(receiptPreviousFences, recoveryFence) == ContainsFence(recoveryPreviousFences, receiptFence);
        public string RecoveryFilmId => recoveryRevision == 0 && HasLocalRecoveryRows() ? catalog.FilmId : recoveryFilm;
        public string RecoveryCompatibilityTag => HasNewerRecoveryRows() ? catalog.CompatibilityTag : recoveryTag;
        public string[] GetRecoverySourceIds() {
            if (!HasNewerRecoveryRows()) return CopyStrings(recoveryIds);
            return GetLocalRecoverySourceIds();
        }
        public string[] GetLocalRecoverySourceIds() {
            if (!HasLocalRecoveryRows()) return new string[0];
            var ids = catalog.GetSourceIds(); var urls = catalog.GetSourceUrls();
            if (recoveryIds.Length == 0) return ids;
            ids[0] = recoveryIds[0];
            for (int i = 1; i < ids.Length; i++) {
                // Legacy snapshots can contain multiple spellings. The writer requires the
                // exact row's ID first, but accepts any retained alias ID for a new spelling.
                int match = -1;
                bool reservedId = ids[i] == recoveryIds[0];
                for (int j = 1; j < recoveryIds.Length; j++) {
                    if (SameUrl(urls[i], recoveryUrls[j])) { match = j; break; }
                    bool retainedId = ids[i] == recoveryIds[j];
                    if (retainedId) reservedId = true;
                    if ((match < 0 || retainedId) && catalog.SameSourceEndpoint(urls[i], recoveryUrls[j])) match = j;
                }
                if (match >= 0) ids[i] = recoveryIds[match];
                else if (reservedId) ids[i] = Guid.NewGuid().ToString("N");
            }
            return ids;
        }
        public string[] GetRecoverySourceLabels() => HasNewerRecoveryRows() ? catalog.GetSourceLabels() : CopyStrings(recoveryLabels);
        public VRCUrl[] GetRecoverySourceUrls() => HasNewerRecoveryRows() ? catalog.GetSourceUrls() : CopyUrls(recoveryUrls);
        public bool IsReadyForPublication => CanPublishReviewedRecovery && !HasRecoveryConflict;
        public bool CanPublishReviewedRecovery => enablePublication && GraphReady() && !frozen &&
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
        bool MatchesFirstPlayback() => !string.IsNullOrEmpty(firstPlayback) &&
            core.LocalStopRevisionForUfc == firstStopRevision && core.SharedPlaybackMarkerForUfc == firstMarker &&
            MatchesCoreContext(firstPlayback, firstPc, firstMobile, firstBackend);
        bool FreshLaunch() => MatchesFirstPlayback() || (core.HasSharedPlaybackContextForUfc && !challengeConsumed &&
            !string.IsNullOrEmpty(challenge) && core.SharedPlaybackMarkerForUfc == challenge &&
            core.SharedPlaybackIdForUfc != blockedPlayback);
        bool WriterNeedsFirstMarker() => enablePublication && !frozen && !writerBase && !hasReceipt &&
            !awaitingHandoffCommit && !handoffCommitted && OwnerAligned() && !catalog.HasAcceptedSnapshot &&
            IsCurrentChallenge() && core.HasSharedPlaybackContextForUfc &&
            string.IsNullOrEmpty(core.SharedPlaybackMarkerForUfc);
        bool WriterReady() => enablePublication && OwnerAligned() && !frozen && (writerBase || FreshLaunch());
        public bool MatchesCoreContext(string playbackId, VRCUrl pc, VRCUrl mobile, byte backend) =>
            Utilities.IsValid(core) && core.HasSharedPlaybackContextForUfc && !string.IsNullOrEmpty(playbackId) &&
            core.SharedPlaybackIdForUfc == playbackId && core.ConfiguredPlayerTypeForUfc == backend &&
            SameUrl(core.CanonicalPcUrlForUfc, pc) && SameUrl(core.EffectiveMobileUrlForUfc, mobile);

        void Start() { Attach(); _RefreshCatalogAuthority(); QueueReconcile(); }
        void OnEnable() { Attach(); }
        void OnDisable() {
            if (registered && Utilities.IsValid(core)) core.UnregisterSharedPlaybackObserverForUfc(this);
            ClearLocalSubscriptions();
            registered = false; writerBase = false; challengeConsumed = false; firstPlayback = "";
            retryAwaitingHeader = retryQueued = rebindRetryQueued = contextPublicationDeferred = false;
            lastUnmarkedPlayback = carryFromPlayback = carryToPlayback = "";
            challenge = observedChallenge = pendingFirstMarker = consumedFirstMarker = ""; CancelPending(Unconfirmed);
        }
        void Attach() {
            if (!GraphReady()) return;
            CacheRecoveryOffer();
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
                challenge = (++nextChallengeGeneration) + ":" + Guid.NewGuid().ToString("N");
                challengeConsumed = false;
                challengeWriter = OwnerId(); wireChallenge = challenge;
                BeginSerialization(true);
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
            if (MatchesFirstPlayback()) return;
            // A missing header may return with the same marked launch. Keep that exact
            // proof while preparing a new marker for the next explicit launch.
            if (core.HasSharedPlaybackContextForUfc || core.SharedStateForUfc == 0 ||
                core.LocalStopRevisionForUfc != firstStopRevision) firstPlayback = "";
            if (FreshLaunch()) {
                firstPlayback = core.SharedPlaybackIdForUfc; firstMarker = challenge;
                firstPc = core.CanonicalPcUrlForUfc; firstMobile = core.EffectiveMobileUrlForUfc;
                firstBackend = core.ConfiguredPlayerTypeForUfc; firstStopRevision = core.LocalStopRevisionForUfc;
                challengeConsumed = true;
                return;
            }
            if (!challengeConsumed) return;
            challengeConsumed = false; challenge = ""; reconcileAttempts = 0;
            _RefreshCatalogAuthority(); QueueReconcile();
        }
        [NetworkCallable(maxEventsPerSecond: 1)]
        public void CatalogRecoveryChallenge(int writerId, string nonce) {
            var actor = NetworkCalling.CallingPlayer;
            if (!NetworkCalling.InNetworkCall || !Utilities.IsValid(actor) || actor.playerId != writerId ||
                writerId != OwnerId() || !Token(nonce, 128)) return;
            ObserveChallenge(writerId, nonce);
            ReconcileChallenge(); QueueReconcile();
        }
        void ReconcileChallenge() {
            int owner = OwnerId();
            if (challengeWriter == owner && Token(wireChallenge, 128) &&
                (deferredChallengeWriter != owner || wireChallenge != deferredChallenge))
                ObserveChallenge(owner, wireChallenge);
            if (RouteReady() && deferredChallengeWriter == owner && Token(deferredChallenge, 128)) ApplyChallenge(deferredChallenge);
        }
        void ObserveChallenge(int writer, string nonce) {
            int generation = 0, end = nonce.IndexOf(':');
            if (end > 0 && (!int.TryParse(nonce.Substring(0, end), out generation) || generation < 0)) return;
            if (deferredChallengeWriter == writer && (generation < deferredGeneration ||
                (generation > 0 && generation == deferredGeneration && nonce != deferredChallenge))) return;
            deferredChallengeWriter = writer; deferredChallenge = nonce; deferredGeneration = generation;
        }
        void QueueReconcile() {
            if (reconcileQueued || !enabled) return;
            bool slow = reconcileAttempts >= 60;
            if (slow && (!enablePublication || !GraphReady() || RouteReady())) return;
            reconcileQueued = true; SendCustomEventDelayedSeconds(nameof(_ReconcileCatalog), slow ? 5f : .5f);
        }
        public void _ReconcileCatalog() {
            if (NetworkCalling.InNetworkCall) return;
            reconcileQueued = false;
            if (!enabled) return;
            if (reconcileAttempts < 60) reconcileAttempts++;
            if (!GraphReady()) { QueueReconcile(); return; }
            ReconcileConsumedChallenge();
            ReconcileChallenge();
            StageRecoveryBase();
            // Synced fields can arrive before the local ownership callback.
            // A returning player can have a new writer fence even if the viewer
            // never received the intervening owner's catalog.
            int slot = IncomingSlot(OwnerId());
            if (!hasIncoming || (wireWriter == OwnerId() && (slot < 0 ||
                rxFence[slot] != wireFence || rxRevision[slot] < wireRevision))) StageWire();
            TryApplyIncoming(); TryAckOffer();
            if (contextPublicationDeferred) { PublishContextCatalog(); StageRecoveryBase(); }
            if (!Networking.IsOwner(gameObject) || (OwnerAligned() && !writerBase && string.IsNullOrEmpty(challenge)))
                _RefreshCatalogAuthority();
            if (!RouteReady() || awaitingHandoffCommit || (hasIncoming && !catalog.IsCurrent)) QueueReconcile();
        }
        public void _OnSharedPlaybackContextForUfc() {
            if (!GraphReady()) return;
            if (core.HasSharedPlaybackContextForUfc) {
                var marker = core.SharedPlaybackMarkerForUfc;
                if (string.IsNullOrEmpty(marker)) {
                    lastUnmarkedPlayback = core.SharedPlaybackIdForUfc;
                    carryFromPlayback = carryToPlayback = "";
                } else if (marker != consumedFirstMarker) {
                    consumedFirstMarker = marker;
                    // Record the authenticated transition even if every editor is closed.
                    bool advised = Networking.IsOwner(gameObject) ? FreshLaunch() && !writerBase :
                        status == NotAuthorityReady && pendingWriter == OwnerId() && pendingPlayback == lastUnmarkedPlayback &&
                        pendingRole == policy.GetRoleRevision() && pendingFirstMarker == marker && observedChallenge == marker;
                    carryFromPlayback = advised ? lastUnmarkedPlayback : "";
                    carryToPlayback = advised ? core.SharedPlaybackIdForUfc : "";
                    carryRole = policy.GetRoleRevision();
                    lastUnmarkedPlayback = "";
                }
            }
            TraceProtocol("core-before", core.SharedPlaybackIdForUfc, catalog.Version, "", -1, 0);
            // The header can disappear while the writer's snapshot is in flight. Keep the
            // request eligible for its exact receipt unless playback stops or changes.
            if ((publicationPending || awaitingLateReceipt) && (core.LocalStopRevisionForUfc != pendingStopRevision ||
                (core.HasSharedPlaybackContextForUfc ? !MatchesCoreContext(pendingPlayback, pendingPc, pendingMobile, pendingBackend) :
                    core.SharedStateForUfc == 0))) CancelPending(Unconfirmed);
            // Once the editor leaves a playback, a delayed header for that old playback
            // cannot turn its superseded request into an exact-current confirmation.
            for (int i = 0; i < lateTargets.Length; i++)
                if (lateTargets[i] != null && (core.LocalStopRevisionForUfc != lateStops[i] ||
                    (core.HasSharedPlaybackContextForUfc ? core.SharedPlaybackIdForUfc != latePlaybacks[i] :
                        core.SharedStateForUfc == 0))) lateExact[i] = false;
            // Staged offers only describe one launch. Release those for the playback
            // being replaced, leaving offers for unobserved launches intact.
            string previousPlayback = catalog.ContextPlaybackId;
            if (!string.IsNullOrEmpty(previousPlayback) && core.HasSharedPlaybackContextForUfc &&
                previousPlayback != core.SharedPlaybackIdForUfc) {
                for (int i = 0; i < rxRecoveryPlayback.Length; i++)
                    if (rxRecoveryPlayback[i] == previousPlayback) ClearIncomingSlot(i);
            }
            applying = true;
            if (core.HasSharedPlaybackContextForUfc)
                catalog.BindSharedContext(this, core.SharedPlaybackIdForUfc, core.CanonicalPcUrlForUfc,
                    core.EffectiveMobileUrlForUfc, core.ConfiguredPlayerTypeForUfc);
            else catalog.ClearSharedContext(this);
            applying = false;
            TryApplyIncoming();
            ReconcileConsumedChallenge();
            PublishContextCatalog();
            StageRecoveryBase();
            if (retryAwaitingHeader && !MissingSerializationHeader()) {
                retryAwaitingHeader = false;
                if (retryQueued) SendCustomEventDelayedFrames(nameof(_RetryCatalogSerialization), 1);
                else if (rebindRetryQueued) SendCustomEventDelayedFrames(nameof(_RetryReboundCatalog), 1);
            }
            TraceProtocol("core-after", core.SharedPlaybackIdForUfc, catalog.Version, "", -1, 0);
        }

        // The Core only announces a given header once. Revisit that header if the
        // local writer is waiting for the permissions object's owner to resolve.
        void PublishContextCatalog() {
            contextPublicationDeferred = false;
            if (!enablePublication || frozen || !Networking.IsOwner(gameObject) || !core.HasSharedPlaybackContextForUfc) return;
            if (!OwnerAligned()) {
                if (CanWaitForWriterRoute()) { contextPublicationDeferred = true; QueueReconcile(); }
                return;
            }
            if (writerBase) PublishRebind(false);
            OfferRecoveryBase();
        }

        bool CanWaitForWriterRoute() {
            if (!enablePublication || frozen || !GraphReady() || !Networking.IsOwner(gameObject)) return false;
            var target = policy.GetAuthorityPlayer();
            return !Utilities.IsValid(target) || target.isLocal;
        }

        public bool RequestPublishCurrentCatalog(int expectedRoleRevision, string expectedCatalogVersion,
            string expectedPlaybackId, string requestId, VRCUrl[] urls, string[] sourceIds, string[] labels, string[] recordMetadata,
            bool reviewedRecovery = false) {
            if (NetworkCalling.InNetworkCall) { status = SourceMirrorCatalog.LocalOnly; return false; }
            if (publicationPending) { status = Busy; return false; }
            if (!enablePublication || !RouteReady() || !registered || !core.HasSharedPlaybackContextForUfc) {
                status = NotAuthorityReady; return false;
            }
            if (HasRecoveryConflict && (!reviewedRecovery || expectedCatalogVersion != RecoveryBaseVersion)) {
                status = SourceMirrorCatalog.CatalogChanged; return false;
            }
            var me = Networking.LocalPlayer;
            if (!Utilities.IsValid(me) || !Token(requestId, 128) || requestId == lastLocalId) { status = InvalidDraft; return false; }
            int envelope = ValidateEnvelope(expectedCatalogVersion, expectedPlaybackId, requestId, urls, sourceIds, labels, recordMetadata);
            if (envelope != SourceMirrorCatalog.Accepted) { status = envelope; return false; }
            ArchiveLocalConfirmation();
            pendingConfirmation = null; pendingDraft = null; pendingView = null; confirmedLocalRequest = rejectedLocalRequest = ""; pendingFirstMarker = "";
            publicationPending = true; awaitingLateReceipt = false; pendingId = requestId; lastLocalId = requestId;
            pendingVersion = ""; pendingWriter = OwnerId(); pendingActor = me.playerId;
            pendingBase = expectedCatalogVersion; pendingPlayback = expectedPlaybackId; pendingRole = expectedRoleRevision;
            pendingPc = core.CanonicalPcUrlForUfc; pendingMobile = core.EffectiveMobileUrlForUfc; pendingBackend = core.ConfiguredPlayerTypeForUfc;
            pendingStopRevision = core.LocalStopRevisionForUfc;
            pendingDeadline = Time.realtimeSinceStartup + 15f; status = SerializationPending;
            TraceProtocol("request", expectedPlaybackId, expectedCatalogVersion, requestId, pendingWriter, status);
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
            int envelope = ValidateEnvelope(expectedVersion, playbackId, requestId, urls, ids, labels, metadata);
            if (envelope != SourceMirrorCatalog.Accepted) { Reply(actor, requestId, envelope, ""); return; }
            urls = CopyUrls(urls); ids = CopyStrings(ids); labels = CopyStrings(labels); metadata = CopyStrings(metadata);
            if (!WriterReady()) {
                // For an otherwise authorized first draft only, include a diagnostic
                // marker in the rejection. It never grants permission or changes Core.
                bool initial = string.IsNullOrEmpty(expectedVersion) && WriterNeedsFirstMarker() &&
                    core.SharedPlaybackIdForUfc == playbackId && role == policy.GetRoleRevision() &&
                    policy.CanPublishCatalog(actor, role);
                Reply(actor, requestId, NotAuthorityReady, initial ? challenge : ""); return;
            }
            var capturedPolicy = policy; var capturedCore = core; var capturedCatalog = catalog;
            int owner = OwnerId(); string capturedChallenge = challenge, capturedTenure = tenure;
            string capturedVersion = catalog.Version;
            bool capturedWriterBase = writerBase;
            if (role != policy.GetRoleRevision()) { Reply(actor, requestId, RoleChanged, ""); return; }
            if (!policy.CanPublishCatalog(actor, role)) { Reply(actor, requestId, NotLeader, ""); return; }
            if (hasReceipt && receiptEditor == actor.playerId && receiptRequest == requestId) {
                bool duplicate = receiptBase == expectedVersion && catalog.SnapshotPlaybackId == playbackId &&
                    SameDraft(metadata, ids, labels, urls);
                Reply(actor, requestId, duplicate ? CommittedLocal : InvalidDraft, duplicate ? catalog.Version : ""); return;
            }
            if (core.SharedPlaybackIdForUfc != playbackId) { Reply(actor, requestId, SourceMirrorCatalog.PlaybackChanged, ""); return; }
            // A late editor may never have seen the previous film's catalog. Its empty
            // base can create the first list for different media, but cannot replace
            // a current list or edit a retained same-film list without its version.
            string writerVersion = catalog.Version;
            bool reviewedLocalHistory = wireSchema == 3 && wireWriter == owner &&
                wireRecoveryPlayback == playbackId && expectedVersion == "empty:" + wireRequest;
            if (!reviewedLocalHistory && expectedVersion != writerVersion && (expectedVersion != "" || catalog.SnapshotPlaybackId == playbackId ||
                MatchesCoreContext(playbackId, catalog.SnapshotCanonical, catalog.SnapshotMobileCanonical, catalog.SnapshotBackend))) {
                // The requester can be missing the entire last send. Re-advertise
                // the existing bytes without applying any part of the stale draft.
                if (wireWriter == owner && capturedVersion == writerVersion && (catalog.IsCurrent || wireRecoveryPlayback == playbackId))
                    BeginSerialization();
                Reply(actor, requestId, SourceMirrorCatalog.CatalogChanged, ""); return;
            }
            VRCUrl pc = core.CanonicalPcUrlForUfc, mobile = core.EffectiveMobileUrlForUfc;
            byte backend = core.ConfiguredPlayerTypeForUfc;
            // Clone before callbacks and revalidate the actual actor/role/header after them.
            var copyUrls = CopyUrls(urls); var copyIds = CopyStrings(ids); var copyLabels = CopyStrings(labels);
            string film = metadata[0], tag = metadata[1];
            applying = true;
            bool valid = catalog.ValidateSharedDraft(this, writerVersion, playbackId, film, tag, copyIds, copyLabels, copyUrls);
            applying = false;
            if (!valid) { Reply(actor, requestId, catalog.Result, ""); return; }
            if (policy != capturedPolicy || core != capturedCore || catalog != capturedCatalog || OwnerId() != owner ||
                challenge != capturedChallenge || !WriterReady() || !SameCaller(actor, network) ||
                policy.GetRoleRevision() != role || !policy.CanPublishCatalog(actor, role) ||
                !MatchesCoreContext(playbackId, pc, mobile, backend) || catalog.Version != writerVersion ||
                !SameCaller(actor, network) || OwnerId() != owner || policy.GetRoleRevision() != role || !policy.IsAuthoritativeHere() ||
                policy != capturedPolicy || core != capturedCore || catalog != capturedCatalog ||
                !MatchesCoreContext(playbackId, pc, mobile, backend) || catalog.Version != writerVersion ||
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
            status = SerializationPending; BeginSerialization();
            Reply(actor, requestId, CommittedLocal, catalog.Version);
        }
        bool SameDraft(string[] metadata, string[] ids, string[] labels, VRCUrl[] urls) =>
            metadata[0] == catalog.FilmId && metadata[1] == catalog.CompatibilityTag &&
            SameStrings(ids, catalog.GetSourceIds()) && SameStrings(labels, catalog.GetSourceLabels()) && SameUrls(urls, catalog.GetSourceUrls());

        void SetWire(string fence, long revision, string playback, VRCUrl pc, VRCUrl mobile, byte backend,
            string film, string tag, string[] ids, string[] labels, VRCUrl[] urls, int editor, int role, string request, string previous) {
            wireSequence = ++localSequence;
            wireSchema = 1; wireWriter = OwnerId(); wireFence = fence; wireRevision = revision;
            wireRecoveryPlayback = "";
            wirePreviousFences = CopyStrings(receiptPreviousFences);
            wirePlayback = playback; wirePc = pc; wireMobile = mobile; wireBackend = backend;
            wireFilm = film; wireTag = tag; wireIds = CopyStrings(ids); wireLabels = CopyStrings(labels); wireUrls = CopyUrls(urls);
            wireEditor = editor; wireRole = role; wireRequest = request; wireBase = previous;
        }
        // Validate the complete candidate before touching live synced or staged fields.
        int ValidateWireCandidate(int writer, string fence, long revision, string playback, VRCUrl pc, VRCUrl mobile,
            byte backend, string film, string tag, string[] ids, string[] labels, VRCUrl[] urls, string request, string previous,
            bool historical = false, bool staging = false) {
            if (!GraphReady() || writer < 0 || (!staging && OwnerId() >= 0 && writer != OwnerId()) ||
                !Token(fence, 96) || revision <= 0 || !Token(playback, 128) ||
                VRCUrl.IsNullOrEmpty(pc) || VRCUrl.IsNullOrEmpty(mobile) || backend == 0) return InvalidDraft;
            int envelope = ValidateEnvelope(previous, playback, string.IsNullOrEmpty(request) ? "rebind" : request,
                urls, ids, labels, new string[] { film, tag });
            if (envelope != SourceMirrorCatalog.Accepted) return envelope;
            if (!SameUrl(pc, urls[0])) return InvalidDraft;
            if (Bytes(pc.Get()) + Bytes(mobile.Get()) + PayloadBytes(urls, ids, labels) +
                Bytes(film) + Bytes(tag) + 1024 > SourceMirrorCatalog.SnapshotBudget) return SourceMirrorCatalog.PayloadTooLarge;
            if (!historical && hasReceipt && fence == receiptFence && revision < catalog.Revision) return Conflict;
            int slot = IncomingSlot(writer);
            if (!historical && slot >= 0 && rxFence[slot] == fence && revision < rxRevision[slot]) return Conflict;
            return SourceMirrorCatalog.Accepted;
        }
        void OfferRecoveryBase() {
            if (!enablePublication || frozen || !OwnerAligned() || !core.HasSharedPlaybackContextForUfc ||
                (!writerBase && !FreshLaunch()) || catalog.IsCurrent || wireRecoveryPlayback == core.SharedPlaybackIdForUfc) return;
            bool retained = hasReceipt && MatchesCoreContext(core.SharedPlaybackIdForUfc, catalog.SnapshotCanonical,
                catalog.SnapshotMobileCanonical, catalog.SnapshotBackend);
            if (writerBase && retained) return; // A coherent writer uses the normal whole-list rebind.
            if (retained)
                SetWire(receiptFence, catalog.Revision, catalog.SnapshotPlaybackId, catalog.SnapshotCanonical,
                    catalog.SnapshotMobileCanonical, catalog.SnapshotBackend, catalog.FilmId, catalog.CompatibilityTag,
                    catalog.GetSourceIds(), catalog.GetSourceLabels(), catalog.GetSourceUrls(), -1, 0, "", "");
            else {
                // A returning editor may know an earlier launch of this film
                // even when the writer holds a different film or no history.
                SetWire("", 0, "", core.CanonicalPcUrlForUfc, core.EffectiveMobileUrlForUfc,
                    core.ConfiguredPlayerTypeForUfc, "", "", new string[0], new string[0], new VRCUrl[0], -1, 0,
                    Guid.NewGuid().ToString("N"), "");
            }
            wireRecoveryPlayback = core.SharedPlaybackIdForUfc;
            // Older receivers must not mistake a historical offer for a publication.
            wireSchema = retained ? 2 : 3;
            CacheRecoveryOffer();
            BeginSerialization();
        }
        // A writer numbers its own publications. An editor can still be viewing the
        // preceding launch while that writer's next offer is already in flight.
        bool RecoveryOfferIsSuperseded() {
            bool compareOtherLaunches = wireSequence > 0 && wireRecoveryPlayback != core.SharedPlaybackIdForUfc;
            if (recoveryWriter == wireWriter && recoverySequence > wireSequence &&
                (compareOtherLaunches || recoveryPlayback == wireRecoveryPlayback)) return true;
            for (int i = 0; i < rxWriter.Length; i++) {
                if (rxWriter[i] != wireWriter || rxFence[i] == null || rxSequence[i] <= wireSequence) continue;
                string playback = rxRecoveryPlayback[i] ?? rxPlayback[i];
                if (compareOtherLaunches || playback == wireRecoveryPlayback) return true;
            }
            return false;
        }

        void RetireRecoveryOffers(string playback, bool published) {
            string current = core.SharedPlaybackIdForUfc;
            for (int i = 0; i < rxWriter.Length; i++) {
                string target = rxRecoveryPlayback[i];
                if (target == null || rxWriter[i] != wireWriter || rxSequence[i] > wireSequence) continue;
                // An unnumbered legacy publication only proves its own launch.
                bool replaced = published && target == playback;
                bool olderLaunch = target != playback && target != current && (!published || wireSequence > 0);
                if (replaced || olderLaunch) ClearIncomingSlot(i);
            }
        }

        // A recovery offer names an earlier list and the launch for which the editor
        // may review it. Delivery can precede both the new owner and that launch.
        void CacheRecoveryOffer() {
            recoveryCaptureDeferred = false;
            if (wireSchema != 2 && wireSchema != 3) return;
            if (!GraphReady()) { recoveryCaptureDeferred = true; return; }
            if (wireWriter < 0 ||
                !Token(wireRecoveryPlayback, 128) || VRCUrl.IsNullOrEmpty(wirePc) ||
                VRCUrl.IsNullOrEmpty(wireMobile) || wireBackend == 0) return;
            if (catalog.IsCurrent && catalog.SnapshotPlaybackId == wireRecoveryPlayback && receiptWriter == wireWriter) return;
            bool retained = wireSchema == 2;
            if (recoveryWriter == wireWriter && recoveryPlayback == wireRecoveryPlayback && recoveryMarker == wireChallenge &&
                (!retained || (recoveryFence == wireFence && recoveryRevision >= wireRevision))) return;
            if (!ValidPreviousFences(wirePreviousFences, wireFence) || RecoveryOfferIsSuperseded()) return;
            int slot = IncomingSlot(wireWriter, wireRecoveryPlayback);
            if (slot >= 0 && rxRecoveryMarker[slot] == wireChallenge &&
                (!retained || (rxFence[slot] == wireFence && rxRevision[slot] >= wireRevision))) return;
            if (retained) {
                if (challengeWriter != wireWriter || !Token(wireChallenge, 128) || wirePlayback == wireRecoveryPlayback ||
                    ValidateWireCandidate(wireWriter, wireFence, wireRevision, wirePlayback, wirePc, wireMobile,
                        wireBackend, wireFilm, wireTag, wireIds, wireLabels, wireUrls, "", "", true, true) != SourceMirrorCatalog.Accepted) return;
            } else if (!Token(wireRequest, 64)) return;

            RetireRecoveryOffers(wireRecoveryPlayback, false);
            slot = ReserveIncomingSlot(wireWriter, wireRecoveryPlayback);
            rxSequence[slot] = wireSequence;
            rxWriter[slot] = wireWriter; rxRecoveryPlayback[slot] = wireRecoveryPlayback; rxRecoveryMarker[slot] = wireChallenge;
            rxPc[slot] = wirePc; rxMobile[slot] = wireMobile; rxBackend[slot] = wireBackend;
            rxFence[slot] = retained ? wireFence : ""; rxRevision[slot] = retained ? wireRevision : 0;
            rxRequest[slot] = retained ? "" : wireRequest;
            rxFilm[slot] = retained ? wireFilm : ""; rxTag[slot] = retained ? wireTag : "";
            rxIds[slot] = retained ? CopyStrings(wireIds) : new string[0];
            rxLabels[slot] = retained ? CopyStrings(wireLabels) : new string[0];
            rxUrls[slot] = retained ? CopyUrls(wireUrls) : new VRCUrl[0];
            rxPreviousFences[slot] = wirePreviousFences == null ? new string[0] : CopyStrings(wirePreviousFences);
        }

        void StageRecoveryBase() {
            if (recoveryCaptureDeferred) CacheRecoveryOffer();
            if (!RouteReady() || !core.HasSharedPlaybackContextForUfc) return;
            int slot = IncomingSlot(OwnerId(), core.SharedPlaybackIdForUfc);
            if (slot < 0) return;
            if (!catalog.IsCurrent) {
                if (!MatchesCoreContext(rxRecoveryPlayback[slot], rxPc[slot], rxMobile[slot], rxBackend[slot]) ||
                    (rxRevision[slot] > 0 && rxRecoveryMarker[slot] != core.SharedPlaybackMarkerForUfc)) return;
                recoverySequence = rxSequence[slot];
                recoveryWriter = rxWriter[slot]; recoveryPlayback = rxRecoveryPlayback[slot]; recoveryMarker = rxRecoveryMarker[slot];
                recoveryPc = rxPc[slot]; recoveryMobile = rxMobile[slot]; recoveryBackend = rxBackend[slot];
                recoveryFence = rxFence[slot]; recoveryRevision = rxRevision[slot];
                recoveryVersion = recoveryRevision > 0 ? recoveryFence + ":" + recoveryRevision : "empty:" + rxRequest[slot];
                recoveryFilm = rxFilm[slot]; recoveryTag = rxTag[slot]; recoveryPreviousFences = rxPreviousFences[slot];
                recoveryIds = rxIds[slot]; recoveryLabels = rxLabels[slot]; recoveryUrls = rxUrls[slot];
            }
            ClearIncomingSlot(slot);
        }

        int IncomingSlot(int writer, string forRecoveryPlayback = null) {
            if (writer < 0) return -1;
            for (int i = 0; i < rxWriter.Length; i++)
                if (rxFence[i] != null && rxWriter[i] == writer && rxRecoveryPlayback[i] == forRecoveryPlayback) return i;
            return -1;
        }

        int ReserveIncomingSlot(int writer, string forRecoveryPlayback = null) {
            int slot = IncomingSlot(writer, forRecoveryPlayback);
            if (slot >= 0) return slot;
            int size = rxWriter.Length;
            for (int i = 0; i < size; i++)
                if (rxFence[i] == null || (rxRecoveryPlayback[i] == null && ContainsFence(receiptPreviousFences, rxFence[i]))) return i;

            // A delayed packet can arrive after its writer left. Reuse that slot under
            // pressure, but never evict another live writer to stage an older update.
            int owner = OwnerId();
            for (int i = 0; i < size; i++)
                if (rxWriter[i] != owner && !Utilities.IsValid(VRCPlayerApi.GetPlayerById(rxWriter[i])) &&
                    !ContainsFence(rxPreviousFences[i], wireFence)) return i;

            int capacity = size * 2;
            rxRecoveryPlayback = (string[])CopyIncoming(rxRecoveryPlayback, new string[capacity]);
            rxRecoveryMarker = (string[])CopyIncoming(rxRecoveryMarker, new string[capacity]);
            rxWriter = (int[])CopyIncoming(rxWriter, new int[capacity]);
            rxEditor = (int[])CopyIncoming(rxEditor, new int[capacity]);
            rxRole = (int[])CopyIncoming(rxRole, new int[capacity]);
            rxSequence = (long[])CopyIncoming(rxSequence, new long[capacity]);
            rxRevision = (long[])CopyIncoming(rxRevision, new long[capacity]);
            rxFence = (string[])CopyIncoming(rxFence, new string[capacity]);
            rxPlayback = (string[])CopyIncoming(rxPlayback, new string[capacity]);
            rxFilm = (string[])CopyIncoming(rxFilm, new string[capacity]);
            rxTag = (string[])CopyIncoming(rxTag, new string[capacity]);
            rxRequest = (string[])CopyIncoming(rxRequest, new string[capacity]);
            rxBase = (string[])CopyIncoming(rxBase, new string[capacity]);
            rxPc = (VRCUrl[])CopyIncoming(rxPc, new VRCUrl[capacity]);
            rxMobile = (VRCUrl[])CopyIncoming(rxMobile, new VRCUrl[capacity]);
            rxBackend = (byte[])CopyIncoming(rxBackend, new byte[capacity]);
            rxIds = (string[][])CopyIncoming(rxIds, new string[capacity][]);
            rxLabels = (string[][])CopyIncoming(rxLabels, new string[capacity][]);
            rxPreviousFences = (string[][])CopyIncoming(rxPreviousFences, new string[capacity][]);
            rxUrls = (VRCUrl[][])CopyIncoming(rxUrls, new VRCUrl[capacity][]);
            return size;
        }

        Array CopyIncoming(Array previous, Array expanded) {
            Array.Copy(previous, expanded, previous.Length);
            return expanded;
        }
        bool StageWire() {
            if (wireSchema != 1 || ContainsFence(receiptPreviousFences, wireFence) || !ValidPreviousFences(wirePreviousFences, wireFence) ||
                ValidateWireCandidate(wireWriter, wireFence, wireRevision, wirePlayback,
                wirePc, wireMobile, wireBackend, wireFilm, wireTag, wireIds, wireLabels, wireUrls,
                wireRequest, wireBase, false, true) != SourceMirrorCatalog.Accepted) return false;
            int slot = ReserveIncomingSlot(wireWriter);
            if (rxRecoveryPlayback[slot] == null && rxWriter[slot] == wireWriter && ContainsFence(rxPreviousFences[slot], wireFence)) return false;
            rxRecoveryPlayback[slot] = rxRecoveryMarker[slot] = null;
            rxWriter[slot] = wireWriter; rxEditor[slot] = wireEditor; rxRole[slot] = wireRole; rxRevision[slot] = wireRevision;
            rxFence[slot] = wireFence; rxPlayback[slot] = wirePlayback; rxPc[slot] = wirePc; rxMobile[slot] = wireMobile; rxBackend[slot] = wireBackend;
            rxFilm[slot] = wireFilm; rxTag[slot] = wireTag; rxRequest[slot] = wireRequest; rxBase[slot] = wireBase;
            rxIds[slot] = CopyStrings(wireIds); rxLabels[slot] = CopyStrings(wireLabels); rxUrls[slot] = CopyUrls(wireUrls);
            rxPreviousFences[slot] = wirePreviousFences == null ? new string[0] : CopyStrings(wirePreviousFences);
            rxSequence[slot] = wireSequence;
            RetireRecoveryOffers(wirePlayback, true);
            hasIncoming = true; return true;
        }
        bool TryApplyIncoming() {
            int slot = IncomingSlot(OwnerId());
            if (slot < 0 || ContainsFence(receiptPreviousFences, rxFence[slot])) { catalogStatus = WaitingForCatalog; return false; }
            return TryApplyCandidate(rxWriter[slot], rxFence[slot], rxRevision[slot], rxPlayback[slot], rxPc[slot], rxMobile[slot], rxBackend[slot],
                rxFilm[slot], rxTag[slot], rxIds[slot], rxLabels[slot], rxUrls[slot], rxEditor[slot], rxRole[slot], rxRequest[slot], rxBase[slot], false, rxPreviousFences[slot]);
        }
        bool TryApplyCandidate(int writer, string fence, long revision, string playback, VRCUrl pc, VRCUrl mobile,
            byte backend, string film, string tag, string[] ids, string[] labels, VRCUrl[] urls,
            int editor, int role, string request, string previous, bool verifiedLocalPublication, string[] predecessors = null) {
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
            bool newEpoch = !hasReceipt || receiptFence != fence;
            // Identity continuity is checked by the writer on every commit. A
            // replica which missed a removal/re-add or an epoch cannot use its
            // older rows as the immediate predecessor of this full snapshot.
            bool knownPredecessor = !hasReceipt || previous == catalog.Version ||
                (!newEpoch && revision - catalog.Revision <= 1);
            bool accepted = catalog.AcceptSharedSnapshot(this, fence, revision, playback,
                film, tag, ids, labels, urls, newEpoch, verifiedLocalPublication || Networking.IsOwner(gameObject) || knownPredecessor);
            applying = false;
            if (!accepted) { catalogStatus = Conflict; return false; }
            catalogStatus = SourceMirrorCatalog.Accepted;
            if (newEpoch) RememberPreviousFences(fence, predecessors);
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
            TraceProtocol("snapshot-before", wirePlayback, wireFence + ":" + wireRevision, wireRequest, wireWriter, 0);
            reconcileAttempts = 0; ReconcileChallenge();
            CacheRecoveryOffer(); StageRecoveryBase();
            if (StageWire()) TryApplyIncoming(); QueueReconcile();
            TraceProtocol("snapshot-after", wirePlayback, wireFence + ":" + wireRevision, wireRequest, wireWriter, catalogStatus);
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
            // Keep the exact accepted edit as the base throughout an uninterrupted chain of
            // automatic same-film copies. Regular writes still use their compare-and-swap base.
            if (!handoff && string.IsNullOrEmpty(receiptRequest) && !string.IsNullOrEmpty(receiptBase)) previous = receiptBase;
            VRCUrl pc = core.CanonicalPcUrlForUfc, mobile = core.EffectiveMobileUrlForUfc;
            byte backend = core.ConfiguredPlayerTypeForUfc;
            var ids = catalog.GetSourceIds(); var labels = catalog.GetSourceLabels(); var urls = catalog.GetSourceUrls();
            if (ValidateWireCandidate(owner, nextTenure, next, playback, pc, mobile, backend, film, tag,
                ids, labels, urls, "", previous) != SourceMirrorCatalog.Accepted ||
                !TryApplyCandidate(owner, nextTenure, next, playback, pc, mobile, backend, film, tag,
                    ids, labels, urls, editor, role, "", previous, false)) return;
            SetWire(nextTenure, next, playback, pc, mobile, backend, film, tag, ids, labels, urls, editor, role, "", previous);
            localSerializedVersion = catalog.Version; localSerializationPending = true;
            StageWire(); writerBase = true; frozen = false; BeginSerialization();
        }

        void Reply(VRCPlayerApi actor, string requestId, int code, string committedVersion) {
            if (!Token(requestId, 128)) return;
            TraceProtocol("reply-send", "", committedVersion, requestId, actor.playerId, code);
            if (actor.isLocal) AcceptReply(actor.playerId, requestId, code, committedVersion);
            else SendCustomNetworkEvent(NetworkEventTarget.All, nameof(CatalogPublishResult), actor.playerId, requestId, code, committedVersion);
        }
        [NetworkCallable(maxEventsPerSecond: 4)]
        public void CatalogPublishResult(int recipientPlayerId, string requestId, int code, string committedVersion) {
            var sender = NetworkCalling.CallingPlayer;
            int senderId = Utilities.IsValid(sender) ? sender.playerId : -1;
            TraceProtocol("reply-before", "", committedVersion, requestId, senderId, code, recipientPlayerId);
            if (!NetworkCalling.InNetworkCall || !Utilities.IsValid(sender) || sender.playerId != OwnerId() ||
                sender.playerId != pendingWriter || !GraphReady()) {
                TraceProtocol("reply-rejected-sender", "", committedVersion, requestId, senderId, code, recipientPlayerId); return;
            }
            if (!RouteReady()) {
                // The RPC sender must still own the catalog and must be the writer to
                // which we submitted this request. An unresolved policy lookup can delay
                // observation of success. Its refusal or send error can release the editor immediately.
                var me = Networking.LocalPlayer;
                var expected = policy.GetAuthorityPlayer();
                if (!Utilities.IsValid(me) || recipientPlayerId != me.playerId ||
                    (Utilities.IsValid(expected) && expected.playerId != senderId)) return;
                if ((code >= SourceMirrorCatalog.NotContextReady && code <= InvalidDraft) || code == Conflict || code == SerializationFailed) {
                    AcceptReply(recipientPlayerId, requestId, code, committedVersion);
                    return;
                }
                if ((code == CommittedLocal || code == SerializationPending) && Token(committedVersion, 128)) {
                    if (requestId == pendingId && pendingActor == me.playerId && string.IsNullOrEmpty(pendingVersion))
                        pendingVersion = committedVersion;
                    else for (int i = 0; i < lateTargets.Length; i++)
                        if (lateTargets[i] != null && lateRequests[i] == requestId && lateWriters[i] == senderId &&
                            string.IsNullOrEmpty(lateVersions[i])) lateVersions[i] = committedVersion;
                    QueueReconcile();
                }
                return;
            }
            AcceptReply(recipientPlayerId, requestId, code, committedVersion);
            TraceProtocol("reply-after", "", committedVersion, requestId, senderId, code, recipientPlayerId);
        }
        void AcceptReply(int recipientPlayerId, string requestId, int code, string committedVersion) {
            var me = Networking.LocalPlayer;
            if (!Utilities.IsValid(me) || recipientPlayerId != me.playerId) return;
            if (requestId != pendingId) { AcceptArchivedReply(requestId, code, committedVersion); return; }
            if (pendingActor != me.playerId || pendingWriter != OwnerId()) return;
            if (!publicationPending && !awaitingLateReceipt) {
                // The new playback header can arrive before the writer's admission reply.
                if (pendingConfirmation != null && code == CommittedLocal &&
                    string.IsNullOrEmpty(pendingVersion) && !string.IsNullOrEmpty(committedVersion)) {
                    pendingVersion = committedVersion;
                    ConfirmPending();
                }
                return;
            }
            if (code == Confirmed) return; // Only the matching snapshot can confirm.
            if (code == CommittedLocal || code == SerializationPending) {
                if (publicationPending) status = code;
                pendingVersion = committedVersion; ConfirmPending();
            } else if (code == SerializationFailed) {
                // An admitted write can still be delivered by a later resend.
                // Release the editor while keeping this exact request eligible for confirmation.
                publicationPending = false; awaitingLateReceipt = true; status = code;
                SetReceiptStatus(pendingConfirmation, SerializationFailed);
                ConfirmPending();
            } else {
                rejectedLocalRequest = requestId;
                if (pendingConfirmation != null) { RejectReceipt(pendingConfirmation, requestId, code); pendingConfirmation = null; }
                pendingView = null;
                pendingFirstMarker = code == NotAuthorityReady && Token(committedVersion, 128) ? committedVersion : "";
                CancelPending(code);
            }
        }
        // Preserve the two-argument API for local subscribers without a form.
        public void TrackLocalConfirmation(string requestId, string[] receipt) =>
            TrackFormConfirmation(requestId, receipt, null);

        public void TrackFormConfirmation(string requestId, string[] receipt, UdonSharpBehaviour view) =>
            TrackFormRetryConfirmation(requestId, receipt, view, "");

        public void TrackFormRetryConfirmation(string requestId, string[] receipt, UdonSharpBehaviour view, string previousRequest) =>
            TrackReceipt(requestId, receipt, view, previousRequest, null);

        public void TrackDraftConfirmation(string requestId, string[] receipt, UdonSharpBehaviour view, string[] deliveredDraft) {
            if (deliveredDraft == null || receipt == null || deliveredDraft.Length != receipt.Length || receipt.Length < 3) return;
            TrackReceipt(requestId, receipt, view, "", deliveredDraft);
        }

        void TrackReceipt(string requestId, string[] receipt, UdonSharpBehaviour view, string previousRequest, string[] draft) {
            if (NetworkCalling.InNetworkCall || string.IsNullOrEmpty(requestId) ||
                requestId != pendingId || receipt == null || (receipt.Length < 1 || (draft == null && receipt.Length > 3))) return;
            if (Utilities.IsValid(view))
                for (int i = 0; i < lateTargets.Length; i++)
                    if (lateTargets[i] != null && lateViews[i] == view && lateRequests[i] != previousRequest &&
                        (draft == null || lateDrafts[i] != draft)) ClearLateSlot(i);
            if (confirmedLocalRequest == requestId) ConfirmReceipt(receipt, requestId, draft);
            else if (rejectedLocalRequest == requestId) RejectReceipt(receipt, requestId, status);
            else if (publicationPending || awaitingLateReceipt) {
                pendingConfirmation = receipt; pendingDraft = draft;
                pendingView = view;
                if (awaitingLateReceipt && (status == SerializationFailed || status == Unconfirmed)) SetReceiptStatus(receipt, status);
            }
        }

        void ArchiveLocalConfirmation() {
            if (pendingConfirmation == null || !Utilities.IsValid(pendingView)) return;
            int slot = -1, recyclable = -1;
            for (int i = 0; i < lateTargets.Length; i++) {
                int index = (nextLateSlot + i) % lateTargets.Length;
                if (lateTargets[index] == null) { slot = index; break; }
                if (recyclable < 0 && lateDrafts[index] == null) recyclable = index;
            }
            // Legacy subscriptions use the rolling archive. Every unresolved attempt from a
            // live form draft can still win its base, even after more than eight timeouts.
            if (slot < 0) slot = recyclable >= 0 ? recyclable : GrowLateArchive();
            lateDrafts[slot] = pendingDraft;
            lateTargets[slot] = pendingConfirmation; lateViews[slot] = pendingView;
            lateRequests[slot] = pendingId; lateBases[slot] = pendingBase; lateVersions[slot] = pendingVersion;
            latePlaybacks[slot] = pendingPlayback; lateWriters[slot] = pendingWriter;
            lateRoles[slot] = pendingRole; lateStops[slot] = pendingStopRevision;
            lateExact[slot] = awaitingLateReceipt;
            nextLateSlot = (slot + 1) % lateTargets.Length;
        }

        Array CopyToLargerArray(Array original, Array larger) {
            Array.Copy(original, larger, original.Length);
            return larger;
        }

        int GrowLateArchive() {
            int firstFree = lateTargets.Length, size = firstFree * 2;
            lateTargets = (string[][])CopyToLargerArray(lateTargets, new string[size][]);
            lateDrafts = (string[][])CopyToLargerArray(lateDrafts, new string[size][]);
            lateViews = (UdonSharpBehaviour[])CopyToLargerArray(lateViews, new UdonSharpBehaviour[size]);
            lateRequests = (string[])CopyToLargerArray(lateRequests, new string[size]);
            lateBases = (string[])CopyToLargerArray(lateBases, new string[size]);
            latePlaybacks = (string[])CopyToLargerArray(latePlaybacks, new string[size]);
            lateVersions = (string[])CopyToLargerArray(lateVersions, new string[size]);
            lateWriters = (int[])CopyToLargerArray(lateWriters, new int[size]);
            lateRoles = (int[])CopyToLargerArray(lateRoles, new int[size]);
            lateStops = (int[])CopyToLargerArray(lateStops, new int[size]);
            lateExact = (bool[])CopyToLargerArray(lateExact, new bool[size]);
            return firstFree;
        }

        public void ReleaseFormDraft(UdonSharpBehaviour view, string[] draft) {
            if (NetworkCalling.InNetworkCall || !Utilities.IsValid(view) || draft == null) return;
            for (int i = 0; i < lateTargets.Length; i++)
                if (lateViews[i] == view && lateDrafts[i] == draft) ClearLateSlot(i);
            if (pendingView == view && pendingDraft == draft) pendingDraft = null;
        }

        void ClearLateSlot(int slot) {
            lateDrafts[slot] = null; lateTargets[slot] = null; lateViews[slot] = null;
            lateRequests[slot] = lateBases[slot] = lateVersions[slot] = latePlaybacks[slot] = "";
        }

        void ClearLocalSubscriptions() {
            pendingConfirmation = null; pendingDraft = null; pendingView = null;
            for (int i = 0; i < lateTargets.Length; i++) ClearLateSlot(i);
        }

        // The form retains its own outcome while this request is eligible for a late snapshot.
        void SetReceiptStatus(string[] receipt, int code) {
            if (receipt != null && receipt.Length >= 2) receipt[1] = code.ToString();
        }

        // Forms also use the exact delivered version when retaining an earlier save across a retry.
        void ConfirmReceipt(string[] receipt, string requestId, string[] draft) {
            receipt[0] = requestId;
            if (receipt.Length >= 3) receipt[2] = string.IsNullOrEmpty(receiptRequest) ? receiptBase : catalog.Version;
            // Only a verified delivery updates the draft's common receipt. A refusal applies to its own attempt.
            if (draft != null) Array.Copy(receipt, draft, receipt.Length);
        }

        public bool IsCurrentPublicationOrReplay(string acceptedVersion) =>
            hasReceipt && !string.IsNullOrEmpty(acceptedVersion) && catalog.IsCurrent && receiptWriter == OwnerId() &&
            (!Networking.IsOwner(gameObject) || (!localSerializationPending && localSerializedVersion == catalog.Version)) &&
            (acceptedVersion == catalog.Version || (string.IsNullOrEmpty(receiptRequest) && receiptBase == acceptedVersion));

        void RejectReceipt(string[] receipt, string requestId, int code) {
            receipt[0] = "-" + requestId;
            SetReceiptStatus(receipt, code);
        }
        void AcceptArchivedReply(string requestId, int code, string version) {
            for (int i = 0; i < lateTargets.Length; i++) {
                if (lateTargets[i] == null || lateRequests[i] != requestId || lateWriters[i] != OwnerId()) continue;
                if (code == CommittedLocal || code == SerializationPending) {
                    if (string.IsNullOrEmpty(lateVersions[i])) lateVersions[i] = version;
                    ConfirmArchived();
                } else if (code == SerializationFailed) {
                    SetReceiptStatus(lateTargets[i], SerializationFailed);
                } else if (code != Confirmed) {
                    RejectReceipt(lateTargets[i], requestId, code);
                    ClearLateSlot(i);
                }
                return;
            }
        }

        void ConfirmArchived() {
            var me = Networking.LocalPlayer;
            if (!hasReceipt || !catalog.IsCurrent || !Utilities.IsValid(me) || receiptEditor != me.playerId ||
                receiptWriter != OwnerId() || (Networking.IsOwner(gameObject) && localSerializationPending)) return;
            for (int i = 0; i < lateTargets.Length; i++) {
                if (lateTargets[i] == null || receiptWriter != lateWriters[i] || receiptRole != lateRoles[i]) continue;
                bool exact = lateExact[i] && core.LocalStopRevisionForUfc == lateStops[i] &&
                    receiptRequest == lateRequests[i] && receiptBase == lateBases[i] &&
                    catalog.SnapshotPlaybackId == latePlaybacks[i] &&
                    (string.IsNullOrEmpty(lateVersions[i]) || lateVersions[i] == catalog.Version);
                // An automatic copy carries the originating accepted version through any number
                // of same-film replays. A manual edit, even with identical fields, starts a new origin.
                bool replay = string.IsNullOrEmpty(receiptRequest) && !string.IsNullOrEmpty(lateVersions[i]) &&
                    receiptBase == lateVersions[i] && catalog.SnapshotPlaybackId != latePlaybacks[i] &&
                    (!Networking.IsOwner(gameObject) || localSerializedVersion == catalog.Version);
                if (!exact && !replay) continue;
                ConfirmReceipt(lateTargets[i], lateRequests[i], lateDrafts[i]);
                ClearLateSlot(i);
            }
        }

        void ConfirmPending() {
            ConfirmArchived();
            if (!hasReceipt || !catalog.IsCurrent || pendingWriter != OwnerId() || receiptWriter != pendingWriter ||
                receiptEditor != pendingActor || receiptRole != pendingRole) return;
            if (publicationPending || awaitingLateReceipt) {
                if (receiptRequest != pendingId || receiptBase != pendingBase || catalog.SnapshotPlaybackId != pendingPlayback ||
                    (!string.IsNullOrEmpty(pendingVersion) && pendingVersion != catalog.Version) ||
                    (Networking.IsOwner(gameObject) && localSerializationPending)) return;
                pendingVersion = catalog.Version; publicationPending = awaitingLateReceipt = false; status = Confirmed;
            } else {
                // A replay can coalesce the admitted version and its automatic copy into one snapshot.
                // The closed form still verifies its submitted rows and its current inputs.
                if (pendingConfirmation == null || string.IsNullOrEmpty(pendingVersion) || receiptBase != pendingVersion ||
                    !string.IsNullOrEmpty(receiptRequest) || catalog.SnapshotPlaybackId == pendingPlayback ||
                    !MatchesCoreContext(catalog.SnapshotPlaybackId, pendingPc, pendingMobile, pendingBackend) ||
                    (Networking.IsOwner(gameObject) && (localSerializationPending || localSerializedVersion != catalog.Version))) return;
            }
            confirmedLocalRequest = pendingId;
            if (pendingConfirmation != null) { ConfirmReceipt(pendingConfirmation, pendingId, pendingDraft); pendingConfirmation = null; }
        }
        void CancelPending(int code) {
            if (publicationPending || awaitingLateReceipt) status = code;
            publicationPending = awaitingLateReceipt = false;
        }
        public void _CatalogPublicationTimeout() {
            if (!publicationPending) return;
            float remaining = pendingDeadline - Time.realtimeSinceStartup;
            if (remaining > 0f) { SendCustomEventDelayedSeconds(nameof(_CatalogPublicationTimeout), remaining + .01f); return; }
            publicationPending = false; awaitingLateReceipt = true; status = Unconfirmed;
            SetReceiptStatus(pendingConfirmation, Unconfirmed);
        }
        public override void OnPreSerialization() {
            serializationAttemptVersion = wireFence + ":" + wireRevision;
            serializationAttemptRequest = wireRequest;
            serializationAttemptChallenge = wireChallenge; serializationAttemptRecovery = wireRecoveryPlayback;
            TraceProtocol("send-before", wirePlayback, serializationAttemptVersion, wireRequest, wireWriter, 0);
        }
        public override void OnPostSerialization(SerializationResult result) {
            lastSerializationByteCount = result.byteCount;
            lastSerializationSucceeded = result.success;
            TraceProtocol("send-after", wirePlayback, serializationAttemptVersion, serializationAttemptRequest,
                wireWriter, result.success ? result.byteCount : -1);
            if (!Networking.IsOwner(gameObject) || serializationAttemptVersion != wireFence + ":" + wireRevision ||
                serializationAttemptRequest != wireRequest || serializationAttemptChallenge != wireChallenge ||
                serializationAttemptRecovery != wireRecoveryPlayback) return;
            if (!result.success && serializationRetries < 3) {
                if (!retryQueued) {
                    retryQueued = true;
                    SendCustomEventDelayedSeconds(nameof(_RetryCatalogSerialization), 1f + serializationRetries);
                }
                return;
            }
            retryQueued = false;
            if (result.success) retryAwaitingHeader = rebindRetryQueued = false;
            else if (IsAutomaticPublicationCurrent() || (CanWaitForWriterRoute() && MissingSerializationHeader() &&
                (wireSchema != 1 || string.IsNullOrEmpty(wireRequest)))) QueueSlowRetry();
            if (!localSerializationPending || serializationAttemptVersion != localSerializedVersion) return;
            if (!result.success) {
                var editor = VRCPlayerApi.GetPlayerById(wireEditor);
                if (Utilities.IsValid(editor)) Reply(editor, wireRequest, SerializationFailed, "");
                status = SerializationFailed;
                // Keep this version unconfirmed until it actually serializes.
                return;
            }
            localSerializationPending = false;
            if (wireRequest == "" && status == SerializationFailed && !publicationPending) status = Confirmed;
            if (localSerializedVersion == catalog.Version) ConfirmPending();
        }
        void BeginSerialization(bool challengeOnly = false) {
            serializingChallenge = challengeOnly;
            serializationStopRevision = core.LocalStopRevisionForUfc;
            serializationRetries = 0; retryAwaitingHeader = retryQueued = rebindRetryQueued = false; RequestSerialization();
        }
        bool IsCurrentRecoveryOffer() => (wireSchema == 2 || wireSchema == 3) && wireWriter == OwnerId() &&
            !catalog.IsCurrent && MatchesCoreContext(wireRecoveryPlayback, wirePc, wireMobile, wireBackend) &&
            (wireSchema == 3 || wireChallenge == core.SharedPlaybackMarkerForUfc);
        bool IsCurrentChallenge() => !writerBase && !string.IsNullOrEmpty(challenge) &&
            challengeWriter == OwnerId() && wireChallenge == challenge;
        bool IsAutomaticPublicationCurrent() => GraphReady() && enablePublication && !frozen &&
            (serializingChallenge ? IsCurrentChallenge() : wireSchema == 1 ?
                string.IsNullOrEmpty(wireRequest) && RequiresPublicationRetry : IsCurrentRecoveryOffer());
        void QueueSlowRetry() {
            if (rebindRetryQueued) return;
            rebindRetryQueued = true; SendCustomEventDelayedSeconds(nameof(_RetryReboundCatalog), 15f);
        }
        bool MissingSerializationHeader() => !serializingChallenge && !core.HasSharedPlaybackContextForUfc &&
            core.SharedStateForUfc != 0 && core.LocalStopRevisionForUfc == serializationStopRevision;
        // A timer that fires before Core restores its header is resumed by the Core observer.
        bool WaitForRetryHeader() {
            if (!CanWaitForWriterRoute() || !MissingSerializationHeader()) return false;
            retryAwaitingHeader = true;
            return true;
        }
        public void _RetryCatalogSerialization() {
            if (NetworkCalling.InNetworkCall || !retryQueued || WaitForRetryHeader()) return;
            retryQueued = false;
            if (!serializingChallenge && GraphReady() && core.LocalStopRevisionForUfc != serializationStopRevision) return;
            if (!CanWaitForWriterRoute() || (serializingChallenge && !IsCurrentChallenge())) return;
            if (!serializingChallenge && (wireSchema == 2 || wireSchema == 3) && !IsCurrentRecoveryOffer()) return;
            if (!serializingChallenge && wireSchema == 1 && localSerializationPending && !RequiresPublicationRetry) return;
            if (!OwnerAligned()) {
                retryQueued = true; SendCustomEventDelayedSeconds(nameof(_RetryCatalogSerialization), 5f); return;
            }
            serializationRetries++; RequestSerialization();
        }
        public void _RetryReboundCatalog() {
            if (NetworkCalling.InNetworkCall || !rebindRetryQueued || WaitForRetryHeader()) return;
            rebindRetryQueued = false;
            if (!serializingChallenge && GraphReady() && core.LocalStopRevisionForUfc != serializationStopRevision) return;
            if (!CanWaitForWriterRoute() || serializationRetries < 3 || !IsAutomaticPublicationCurrent()) return;
            if (OwnerAligned()) RequestSerialization();
            else QueueSlowRetry();
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
            ClearLocalSubscriptions();
            CancelPending(Unconfirmed); localSerializationPending = false; retryAwaitingHeader = retryQueued = rebindRetryQueued = contextPublicationDeferred = false;
            int nextOwner = Utilities.IsValid(newOwner) ? newOwner.playerId : -1;
            if (recoveryWriter != nextOwner) recoveryWriter = -1;
            bool cooperativeDeparture = frozen && nextOwner == offerTarget && catalog.Version == offerVersion &&
                Time.realtimeSinceStartup <= handoffDeadline;
            if (!cooperativeDeparture) handoffCommitted = false;
            handoffAccepted = Utilities.IsValid(newOwner) && newOwner.isLocal && offeredBase && handoffCommitReceived &&
                nextOwner == offerTarget && catalog.Version == offerVersion && hasReceipt &&
                Time.realtimeSinceStartup <= handoffDeadline;
            awaitingHandoffCommit = Utilities.IsValid(newOwner) && newOwner.isLocal && offeredBase && !handoffAccepted &&
                nextOwner == offerTarget && Time.realtimeSinceStartup <= handoffDeadline;
            writerBase = false; challengeConsumed = false; firstPlayback = "";
            lastUnmarkedPlayback = carryFromPlayback = carryToPlayback = "";
            challenge = observedChallenge = pendingFirstMarker = consumedFirstMarker = ""; observedOwner = nextOwner;
            if (!handoffAccepted) { tenure = ""; frozen = cooperativeDeparture; if (!awaitingHandoffCommit) offeredBase = false; }
            SendCustomEventDelayedFrames(nameof(_RefreshCatalogAuthority), 2);
            reconcileAttempts = 0; QueueReconcile();
        }
        public override void OnMasterTransferred(VRCPlayerApi newMaster) {
            reconcileAttempts = 0; QueueReconcile(); SendCustomEventDelayedFrames(nameof(_RefreshCatalogAuthority), 3);
        }
        public override void OnPlayerLeft(VRCPlayerApi player) {
            if (!Utilities.IsValid(player)) return;
            for (int i = 0; i < rxWriter.Length; i++)
                if (rxWriter[i] == player.playerId) ClearIncomingSlot(i);
        }
        void ClearIncomingSlot(int slot) {
            rxWriter[slot] = -1;
            rxRecoveryPlayback[slot] = rxRecoveryMarker[slot] = null;
            rxFence[slot] = rxPlayback[slot] = rxFilm[slot] = rxTag[slot] = rxRequest[slot] = rxBase[slot] = null;
            rxPc[slot] = rxMobile[slot] = null;
            rxIds[slot] = rxLabels[slot] = rxPreviousFences[slot] = null;
            rxUrls[slot] = null;
        }
        public override void OnPlayerJoined(VRCPlayerApi player) {
            reconcileAttempts = 0; QueueReconcile();
            if (enablePublication && Networking.IsOwner(gameObject) && GraphReady())
                BeginSerialization(IsCurrentChallenge() && !IsCurrentRecoveryOffer());
        }

        int ValidateEnvelope(string version, string playback, string request, VRCUrl[] urls, string[] ids, string[] labels, string[] metadata) {
            if (version == null || version.Length > 128 || !Token(playback, 128) || !Token(request, 128) ||
                metadata == null || metadata.Length != 2 || string.IsNullOrEmpty(metadata[0]) || metadata[0].Length > 64 ||
                !Id(metadata[0]) || string.IsNullOrEmpty(metadata[1]) || metadata[1].Length > 128 || urls == null || ids == null || labels == null ||
                urls.Length < 1 || urls.Length > 3 || ids.Length != urls.Length || labels.Length != urls.Length) return InvalidDraft;
            for (int i = 0; i < urls.Length; i++) {
                if (VRCUrl.IsNullOrEmpty(urls[i]) || !Id(ids[i]) || labels[i] == null || labels[i].Length > 128) return InvalidDraft;
                for (int j = 0; j < i; j++) if (ids[i] == ids[j] || SameUrl(urls[i], urls[j])) return InvalidDraft;
            }
            return PayloadBytes(urls, ids, labels) + Bytes(version) + Bytes(playback) + Bytes(request) +
                Bytes(metadata[0]) + Bytes(metadata[1]) + 512 <= SourceMirrorCatalog.DraftBudget
                ? SourceMirrorCatalog.Accepted : SourceMirrorCatalog.PayloadTooLarge;
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
        bool ContainsFence(string[] fences, string fence) {
            if (fences == null || string.IsNullOrEmpty(fence)) return false;
            for (int i = 0; i < fences.Length; i++) if (fences[i] == fence) return true;
            return false;
        }
        bool ValidPreviousFences(string[] fences, string current) {
            if (fences == null) return true; // Older snapshots did not carry this field.
            if (fences.Length > MaxPreviousFences) return false;
            for (int i = 0; i < fences.Length; i++)
                if (fences[i] == current || !Token(fences[i], 96) || Bytes(fences[i]) > 96) return false;
            return true;
        }
        void RememberPreviousFences(string current, string[] advertised) {
            var recent = new string[MaxPreviousFences]; int count = 0;
            if (hasReceipt && receiptFence != current && Bytes(receiptFence) <= 96 && !ContainsFence(advertised, receiptFence))
                recent[count++] = receiptFence;
            // The writer knows its predecessors; this peer may also have seen
            // another intervening owner that the writer missed before joining.
            for (int pass = 0; pass < 2; pass++) {
                var source = pass == 0 ? advertised : receiptPreviousFences;
                if (source == null) continue;
                for (int i = 0; i < source.Length && count < MaxPreviousFences; i++)
                    if (source[i] != current && !ContainsFence(recent, source[i])) recent[count++] = source[i];
            }
            receiptPreviousFences = new string[count];
            for (int i = 0; i < count; i++) receiptPreviousFences[i] = recent[i];
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
