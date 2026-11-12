# Cinema.7 follow-up qualification

Status: PASS for the bounded coverage below. Source pins and receipt digests are
in the matching JSON, using repository-relative source paths. The three task
commits are merged into the collector. Stable merge/push remains a delivery step.

## Frozen runtime source

- QueueList: the first distinct Quest URL backfills waiting PC-only rows;
  RepeatAll preserves the current PC/Quest pair, including null/empty alternate
  fallback for configured-default Ready and EOF states.
- ImageViewer: active/held loader retirement, current-request fencing, bounded
  disposal/teardown, and Ready/Play reentry protection.
- Core: fallback handler activation notifies texture consumers to retire borrowed
  image references before the next frame. The paused-clock source is unchanged.

## Verified evidence

- Actual-method queue qualification: 56 baseline and 134456 fixed assertions,
  plus 6238 prior regression assertions across PC/Quest compile branches.
- Image actual-method lifecycle: 153 assertions. Core fallback ownership: 27;
  prior Core: 29; retry controls: 41.
- Four full C# modules / 885 inputs pass against all three frozen source hashes.
  Udon records 120 programs / 120 outputs with no compilation errors.
- Native PC compiled queue rotation/remove: 46 assertions using coherent current
  and queue platform pairs seeded at the receive boundary.
- Native SDK image lifetime: 1456 assertions over 1713 real frames / 20.336s;
  nine actual Enqueue, six Next and three Stop actions. Current 404 retry/cancel,
  cache/teardown and SDK-visible result/handle lifetime checks pass.
- Actual shader-global probe sees the old valid texture, then an invalid global
  texture after cleanup. No Core_Screen source edit is included.
- Final preservation passes: six installed writes, 6152 scene transforms, no
  missing scripts; original scene file unchanged, authoring restoration passes
  across 24479 compared objects, original scene never written.

## Coverage limits

The separate native distinct-Quest enqueue control was NOT RUN: the stock native
helper exposes no actual alternate/mobile URL input. The complete actual-body
PC/Quest matrix and seeded compiled platform-pair rotation pass. Native image
Enqueue actions use the available PC input; they do not certify mobile enqueue.

Quest conditional coverage is not a physical Quest run. Seeded owner/receive
states do not establish actual Photon handoff. SDK lifetime checks do not measure
GPU residency. The destroy event boundary was explicitly invoked. Source push,
VPM archive publication and VRChat world upload are separate operations.

Raw receipts remain in the companion Cinema project under
Concept/video_post_merge_fix_20261112. This portable record contains bounded
summaries and receipt digests, without native frame histories.
