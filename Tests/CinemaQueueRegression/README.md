# Cinema queue lifecycle regression qualification

For the Cinema.7 queue and bounded image-lifetime follow-up, see
[README.cinema.7.md](README.cinema.7.md) and
[qualification.cinema.7.json](qualification.cinema.7.json). The Cinema.6 record
below is retained as historical qualification.

This records the qualified `1.8.3-cinema.6` source candidate, including all
previous cinema.2 through cinema.5 queue/Stop/retry corrections. Source hashes
and bounded verification results are in `qualification.json`.

The companion Cinema project owns the native fixtures, actual-body tests and
full receipts under `Tools/Video` and `Concept/video_player_queue_fix_20261112`.
The package fixes cover cancelled Loading, deferred Stop cancellation, cold and
received RepeatAll origins, scoped retry records, malformed UTF-8 title decoding,
empty Core validation, and stale image request callbacks. Companion shell fixes
cover enabled Stop during an error and complete stale Remove/Clear identities.

Verification: four full C# modules,120 Udon programs, PC/Quest method-body
controls, actual native decoder/Next/buttons, and real SDK HTTP image callbacks.
Receive/owner snapshots and video errors are controlled boundaries. Actual
Photon handoff, AVPro and HMD acceptance remain separate client gates.
One image-handle finalizer warning at Play exit is explicitly retained; this
qualification does not claim leak-free teardown or resolution of speed audio clicks.

Promotion changes Git history only: installed runtime bytes, scene and package
GUIDs are unchanged. No VPM archive release or VRChat upload is implied.
