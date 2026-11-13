using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using JLChnToZ.VRC.Foundation.I18N;

namespace JLChnToZ.VRC.VVMW {
    public partial class FrontendHandler {
        [SerializeField, LocalizedLabel] bool enableQueueList = true;
        [UdonSynced] VRCUrl[] queuedUrls, queuedQuestUrls;
        [UdonSynced] string queuedTitles;
        [UdonSynced] byte[] queuedPlayerIndex;
        [UdonSynced] string currentTitle;
        VRCUrl[] localQueuedUrls, localQueuedQuestUrls;
        byte[] localQueuedPlayerIndex;
        string[] localQueuedTitles;
        string localCurrentTitle;

        /// <summary>
        /// The URLs of the queued items.
        /// </summary>
        /// <remarks>The returned array is meant to be read only, do not modify.</remarks>
        public VRCUrl[] QueueUrls {
            get {
                if (!Utilities.IsValid(localQueuedUrls)) localQueuedUrls = new VRCUrl[0];
                return localQueuedUrls;
            }
        }

        /// <summary>
        /// The optional Quest URLs of queued items, empty when every item uses its PC URL.
        /// </summary>
        /// <remarks>The returned array is meant to be read only, do not modify.</remarks>
        public VRCUrl[] QueueQuestUrls {
            get {
                if (!Utilities.IsValid(localQueuedQuestUrls)) localQueuedQuestUrls = new VRCUrl[0];
                return localQueuedQuestUrls;
            }
        }

        /// <summary>
        /// The player backend index of the queued items.
        /// </summary>
        /// <remarks>
        /// This is 1-based index, 0 is invalid.
        /// </remarks>
        /// <remarks>The returned array is meant to be read only, do not modify.</remarks>
        public byte[] QueuePlayerIndex {
            get {
                if (!Utilities.IsValid(localQueuedPlayerIndex)) localQueuedPlayerIndex = new byte[0];
                return localQueuedPlayerIndex;
            }
        }

        /// <summary>
        /// The titles of the queued items.
        /// </summary>
        /// <remarks>The returned array is meant to be read only, do not modify.</remarks>
        public string[] QueueTitles {
            get {
                if (!Utilities.IsValid(localQueuedTitles)) localQueuedTitles = new string[0];
                return localQueuedTitles;
            }
        }

        /// <summary>
        /// Whether the queue list is enabled.
        /// </summary>
        public bool HasQueueList => enableQueueList;

        /// <inheritdoc cref="PlayUrl(VRCUrl, VRCUrl, string, byte)"/>
        public void PlayUrl(VRCUrl url, byte index) => PlayUrl(url, null, null, index);

        /// <inheritdoc cref="PlayUrl(VRCUrl, VRCUrl, string, byte)"/>
        public void PlayUrl(VRCUrl pcUrl, VRCUrl questUrl, byte index) => PlayUrl(pcUrl, questUrl, null, index);

        /// <summary>
        /// Play or enqueue a video URL.
        /// </summary>
        /// <param name="url">The URL of the video.</param>
        /// <param name="pcUrl">The URL of the video for PC.</param>
        /// <param name="questUrl">The URL of the video for Quest (mobile).</param>
        /// <param name="queuedTitle">The title of the video to be displayed in the queue list.</param>
        /// <param name="index">The player index (1-based) to play the video.</param>
        /// <remarks>
        /// If queue list is enabled and the player is playing or loading, the URL will be enqueued.
        /// Except if it is playing an entry of a playlist, in this case will always intrrupts the current playback.
        /// </remarks>
        public void PlayUrl(VRCUrl pcUrl, VRCUrl questUrl, string queuedTitle, byte index) {
            if (Utilities.IsValid(urlInputFilter)) {
                urlInputFilter.pcUrl = pcUrl;
                urlInputFilter.questUrl = questUrl;
                urlInputFilter._ValidateUrls();
                pcUrl = urlInputFilter.pcUrl;
                questUrl = urlInputFilter.questUrl;
            }
            if (VRCUrl.IsNullOrEmpty(pcUrl)) return;
            if (VRCUrl.IsNullOrEmpty(questUrl)) questUrl = pcUrl;
            if (localPlayListIndex > 0) {
                localPlayListIndex = 0;
                localQueuedUrls = null;
                localQueuedQuestUrls = null;
                localQueuedPlayerIndex = null;
                localQueuedTitles = null;
                if (!enableQueueList) core.Stop();
            }
            if (string.IsNullOrEmpty(queuedTitle))
                queuedTitle = $"\u200B{Networking.LocalPlayer.displayName}:\n\u200B{UnescapeUrl(pcUrl)}";
            else if (queuedTitle[0] != '\u200B')
                queuedTitle = $"\u200B{Networking.LocalPlayer.displayName}:\n{queuedTitle}";
            // U+2029 is the legacy wire delimiter for queue/history titles. Keep the paragraph as a newline.
            queuedTitle = queuedTitle.Replace('\u2029', '\n');
            if (enableQueueList) {
                bool isQueueEmpty = IsArrayNullOrEmpty(localQueuedUrls);
                if (core.IsReady || core.IsLoading || core.IsLocalTransportActiveForUfc || !isQueueEmpty) {
                    if (isQueueEmpty) {
                        localQueuedUrls = new VRCUrl[] { pcUrl };
                    } else {
                        var newQueue = new VRCUrl[localQueuedUrls.Length + 1];
                        Array.Copy(localQueuedUrls, newQueue, localQueuedUrls.Length);
                        newQueue[localQueuedUrls.Length] = pcUrl;
                        localQueuedUrls = newQueue;
                    }
                    bool isQuestQueueEmpty = IsArrayNullOrEmpty(localQueuedQuestUrls);
                    if (!pcUrl.Equals(questUrl) || !isQuestQueueEmpty) {
                        if (isQuestQueueEmpty) {
                            // Materializing the optional array must backfill every earlier PC-only row.
                            localQueuedQuestUrls = new VRCUrl[localQueuedUrls.Length];
                            Array.Copy(localQueuedUrls, localQueuedQuestUrls, localQueuedUrls.Length);
                            localQueuedQuestUrls[localQueuedQuestUrls.Length - 1] = questUrl;
                        } else {
                            var newAltQueue = new VRCUrl[localQueuedQuestUrls.Length + 1];
                            Array.Copy(localQueuedQuestUrls, newAltQueue, localQueuedQuestUrls.Length);
                            newAltQueue[localQueuedQuestUrls.Length] = questUrl;
                            localQueuedQuestUrls = newAltQueue;
                        }
                    }
                    if (IsArrayNullOrEmpty(localQueuedPlayerIndex)) {
                        localQueuedPlayerIndex = new byte[] { index };
                    } else {
                        var newPlayerIndexQueue = new byte[localQueuedPlayerIndex.Length + 1];
                        Array.Copy(localQueuedPlayerIndex, newPlayerIndexQueue, localQueuedPlayerIndex.Length);
                        newPlayerIndexQueue[localQueuedPlayerIndex.Length] = index;
                        localQueuedPlayerIndex = newPlayerIndexQueue;
                    }
                    if (IsArrayNullOrEmpty(localQueuedTitles)) {
                        localQueuedTitles = new string[] { queuedTitle };
                    } else {
                        var newTitles = new string[localQueuedTitles.Length + 1];
                        Array.Copy(localQueuedTitles, newTitles, localQueuedTitles.Length);
                        newTitles[localQueuedTitles.Length] = queuedTitle;
                        localQueuedTitles = newTitles;
                    }
                    RequestSync();
                    UpdateState();
                    return;
                }
            }
            RecordPlaybackHistory(pcUrl, questUrl, index, queuedTitle);
            localCurrentTitle = queuedTitle;
            RequestSync();
            // The current queue item exists before Ready, including the first launch.
            localPlayingPlaylistIndex = 0;
            core.PlayUrl(pcUrl, questUrl, index);
            MarkLocalQueuePlaybackForUfc();
            UpdateTitle();
        }

        void PlayQueueList(int index, bool deleteOnly) {
            int newLength = Utilities.IsValid(localQueuedUrls) ? localQueuedUrls.Length : 0;
            if (newLength <= 0) {
                if (!deleteOnly && CanRotateCurrentQueuePlaybackForUfc() && index < 0 && RepeatAll) {
                    GetLastPlayedUrl(out VRCUrl lastPCUrl, out VRCUrl lastQuestUrl, out byte lastActivePlayer);
                    core.PlayUrl(lastPCUrl, lastQuestUrl, lastActivePlayer);
                    MarkLocalQueuePlaybackForUfc();
                    core._ResetTitle();
                    RecordPlaybackHistory(lastPCUrl, lastQuestUrl, lastActivePlayer, localCurrentTitle);
                }
                return;
            }
            if (index >= newLength) return;
            if (index < 0) {
                if (deleteOnly) {
                    localPlayListIndex = 0;
                    localQueuedUrls = null;
                    localQueuedQuestUrls = null;
                    localQueuedPlayerIndex = null;
                    localQueuedTitles = null;
                    RequestSync();
                    UpdateState();
                    return;
                }
                index = Shuffle ? UnityEngine.Random.Range(0, newLength) : 0;
            }
            var shouldReEnqueue = !deleteOnly && CanRotateCurrentQueuePlaybackForUfc() && RepeatAll;
            var questQueue = localQueuedQuestUrls;
            bool hasQuestUrl = !IsArrayNullOrEmpty(questQueue);
            VRCUrl requeuePCUrl = null, requeueQuestUrl = null;
            byte requeuePlayer = 0;
            if (shouldReEnqueue) {
                GetLastPlayedUrl(out requeuePCUrl, out requeueQuestUrl, out requeuePlayer);
                if (VRCUrl.IsNullOrEmpty(requeueQuestUrl)) requeueQuestUrl = requeuePCUrl;
                if (!hasQuestUrl && !VRCUrl.IsNullOrEmpty(requeueQuestUrl) && !requeueQuestUrl.Equals(requeuePCUrl)) {
                    // The current film may need a Quest URL even when all waiting rows use their PC URL.
                    questQueue = new VRCUrl[localQueuedUrls.Length];
                    Array.Copy(localQueuedUrls, questQueue, localQueuedUrls.Length);
                    hasQuestUrl = true;
                }
            }
            if (!shouldReEnqueue) newLength--;
            var url = localQueuedUrls[index];
            var questUrl = hasQuestUrl ? questQueue[index] : url;
            var playerIndex = localQueuedPlayerIndex[index];
            var title = localQueuedTitles[index];
            var newQueue = newLength == localQueuedUrls.Length ? localQueuedUrls : new VRCUrl[newLength];
            var newQuestQueue = hasQuestUrl ? newLength == questQueue.Length ? questQueue : new VRCUrl[newLength] : null;
            var newPlayerIndexQueue = newLength == localQueuedUrls.Length ? localQueuedPlayerIndex : new byte[newLength];
            var newTitles = newLength == localQueuedUrls.Length ? localQueuedTitles : new string[newLength];
            if (index > 0) {
                if (localQueuedUrls != newQueue)
                    Array.Copy(localQueuedUrls, 0, newQueue, 0, index);
                if (hasQuestUrl && questQueue != newQuestQueue)
                    Array.Copy(questQueue, 0, newQuestQueue, 0, index);
                if (localQueuedPlayerIndex != newPlayerIndexQueue)
                    Array.Copy(localQueuedPlayerIndex, 0, newPlayerIndexQueue, 0, index);
                if (localQueuedTitles != newTitles)
                    Array.Copy(localQueuedTitles, 0, newTitles, 0, index);
            }
            int copyCount = Mathf.Min(localQueuedUrls.Length - 1, newLength) - index;
            Array.Copy(localQueuedUrls, index + 1, newQueue, index, copyCount);
            if (hasQuestUrl) Array.Copy(questQueue, index + 1, newQuestQueue, index, copyCount);
            Array.Copy(localQueuedPlayerIndex, index + 1, newPlayerIndexQueue, index, copyCount);
            Array.Copy(localQueuedTitles, index + 1, newTitles, index, copyCount);
            if (shouldReEnqueue) {
                int lastIndex = newLength - 1;
                newQueue[lastIndex] = requeuePCUrl;
                if (hasQuestUrl) newQuestQueue[lastIndex] = requeueQuestUrl;
                newPlayerIndexQueue[lastIndex] = requeuePlayer;
                newTitles[lastIndex] = localCurrentTitle;
            }
            localQueuedUrls = newQueue;
            localQueuedQuestUrls = newQuestQueue;
            localQueuedPlayerIndex = newPlayerIndexQueue;
            localQueuedTitles = newTitles;
            if (!deleteOnly) {
                localCurrentTitle = title;
                // Mark only after deciding whether an older item belongs in this rotation.
                localPlayingPlaylistIndex = 0;
                core.PlayUrl(url, questUrl, playerIndex);
                MarkLocalQueuePlaybackForUfc();
                UpdateTitle();
                RecordPlaybackHistory(url, questUrl, playerIndex, title);
            }
            RequestSync();
            UpdateState();
        }

        void UpdateTitle() {
            if (string.IsNullOrEmpty(localCurrentTitle)) {
                core._ResetTitle();
                return;
            }
            int index = localCurrentTitle.IndexOf('\n');
            if (index <= 0) {
                core.SetTitle(localCurrentTitle, "");
                return;
            }
            index++;
            if (index < localCurrentTitle.Length && localCurrentTitle[index] != '\u200B') {
                core.SetTitle(localCurrentTitle.Substring(index), "");
                return;
            }
            core._ResetTitle();
        }

        void GetLastPlayedUrl(out VRCUrl pcUrl, out VRCUrl questUrl, out byte playerIndex) {
            // Loading skips do not Stop first, so LastUrl can still belong to the previous item.
            if (core.IsReady || core.IsLocalTransportActiveForUfc ||
                (core.IsLoading && core.ActivePlayer != 0 && !VRCUrl.IsNullOrEmpty(core.Url))) {
#if UNITY_ANDROID || UNITY_IOS
                pcUrl = VRCUrl.IsNullOrEmpty(core.AltUrl) ? core.Url : core.AltUrl;
                questUrl = core.Url;
#else
                pcUrl = core.Url;
                questUrl = VRCUrl.IsNullOrEmpty(core.AltUrl) ? core.Url : core.AltUrl;
#endif
                playerIndex = core.ActivePlayer;
            } else {
#if UNITY_ANDROID || UNITY_IOS
                pcUrl = VRCUrl.IsNullOrEmpty(core.LastAltUrl) ? core.LastUrl : core.LastAltUrl;
                questUrl = core.LastUrl;
#else
                pcUrl = core.LastUrl;
                questUrl = VRCUrl.IsNullOrEmpty(core.LastAltUrl) ? core.LastUrl : core.LastAltUrl;
#endif
                playerIndex = core.LastActivePlayer;
            }
        }
    }
}
