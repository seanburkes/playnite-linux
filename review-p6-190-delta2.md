# Reviewer BJ — p6-190 delta 2 (final focused review)

## Outcome: no blocking P1/P2

The delta closes both gaps the brief names. Null dismissal results are recorded as
per-message failures inside the `try` guard and reach `NotificationDismissAllResult.Failures`
and the aggregate diagnostic; a host-originated `OperationCanceledException` is recorded as a
host failure and the loop continues, while caller cancellation still propagates. The three
changed files are the implementation, its tests and the unit page; the only deleted lines are
documentation text.

This is a static review. Per the brief I did not build, run tests, or read any other review.
I read the supplied delta only, plus the head blobs for line/context verification. The PR head
was read back from the forge; CI at that head reports 8 passed / 0 failed. The build/test counts
in the unit page are therefore the page's and CI's, not independently reproduced here.

## Review identity

| Item | Value |
| --- | --- |
| PR | https://github.com/seanburkes/playnite-linux/pull/190 |
| Base → head | `f339b37f8da74e547934afdb98857ab3ebcd3da3` → `9dd1fe6e374f86c1679a3cd0cecf0f6350ccea26` |
| Delta input | `/home/sean/src/firstmate/data/p6-190/delta2.diff` |
| SHA-256 | `2b21acd00213221a05659b190e0e6240d4f7d3816ae49643612351e006886b55` |
| Shape | 3 files, +153 / −8 |

Binding checks:

- `sha256sum` of the supplied delta is `2b21acd0…86b55`, as stated in the brief.
- `git diff f339b37 9dd1fe6` in this worktree is **byte-identical** to the supplied file
  (same SHA-256), so the reviewed bytes are the commit range and not a hand-edited excerpt.
- `9dd1fe6^` is `f339b37`, so the range is exactly the one delta commit.
- `gh-axi pr view 190 --fields …` reports `headRefOid: 9dd1fe6e374f86c1679a3cd0cecf0f6350ccea26`,
  base `main`, state `OPEN`, `isDraft: false` — the reviewed head is the PR head.
- `gh-axi pr checks 190` at that head: `8 passed, 0 failed, 8 total` (linux build/test,
  packaging smokes, guard, ownership, formal, evidence, inventory/parity).

Line numbers below are new-file lines at `9dd1fe6`.

## Verified claims

1. **Null host result is a per-message failure inside the guard and is aggregated.**
   `src/Playnite.Core/Hosting/Notifications.cs:414-423`: the check is inside the `try`, so a
   null result cannot reach the `result.WasPresented` dereference at `:451`. It appends a
   `NotificationDismissFailure` (`Failed`, `Threw: false`, fixed diagnostic), appends the same
   text to `diagnostics`, and `continue`s, so the remaining ids are still processed. The
   aggregate at `:463-467` includes it in `Failures` and in the joined diagnostic because
   `failures.Count`/`diagnostics` cover every branch.
   `tests/Playnite.Core.Tests/Hosting/NotificationsApiTests.cs:432-457` asserts
   `Dismissed == 3`, one failure for `two`, `Status == Failed`, `Threw == false`, the exact
   diagnostic, the aggregate diagnostic containing `two`, and `host.Dismissed` containing all
   three ids — i.e. the aggregate result and the continued loop, not just the list entry.

2. **Host-thrown OCE is distinguished from caller cancellation.**
   `Notifications.cs:425-435` catches `OperationCanceledException` only when
   `!cancellationToken.IsCancellationRequested`, records it (`Failed`, `Threw: true`,
   `exception.Message`) and continues. `Notifications.cs:436-439` keeps the unconditional
   rethrow for a canceled caller token, and `:409` keeps the per-iteration
   `ThrowIfCancellationRequested`. `NotificationsApiTests.cs:460-484` throws the OCE
   synchronously on the middle id and asserts the failure plus all three `host.Dismissed`
   entries; `:487-529` mixes null, returned-`Unavailable`, thrown `InvalidOperationException`
   and host OCE in one aggregate and asserts the ordered statuses and `Threw` flags.

3. **Caller cancellation still stops the aggregate.**
   `NotificationsApiTests.cs:553-571` cancels the token inside the first host call and expects
   the call to throw with only one host dismissal. The pre-existing
   `DismissAllCancellationThrowsAfterClearingTheLocalList` (`:531-551`) cancels the token and
   then lets the host's `Task.Delay(…, token)` throw a `TaskCanceledException`; the new filter
   evaluates false and the rethrow path is taken, so that test still passes and would fail if
   the filter were dropped. The distinction is therefore covered in both directions.

4. **No other contract change.**
   The range is exactly three files; the only code change is the 21 added lines at
   `Notifications.cs:414-434` and the only deletions are the documentation lines replaced at
   `docs/port/p6-006-notifications-dialogs-mainview.md:45-52,88`. Page verification counts move
   from 63→67 Hosting and 2,872→2,876 full, consistent with exactly four added `[Fact]`s
   (`:431,459,486,553`) and no removed tests.

## Non-blocking notes

### P3 — the new caller-cancel test does not fail on the prior behaviour

**Location:** `tests/Playnite.Core.Tests/Hosting/NotificationsApiTests.cs:554-571`, specifically
the handler at `:559-563`.

The handler cancels the token and then **returns a successful result**; it never throws an
`OperationCanceledException`. On the prior code (`f339b37:src/Playnite.Core/Hosting/Notifications.cs`,
the same `ThrowIfCancellationRequested` at the top of the loop, `:409` here) the second iteration
also throws, and both `Assert.Single(host.Dismissed)` and `Assert.Empty(api.Messages)` hold. The
test therefore passes unchanged against `f339b37`: it is a guard for pre-existing behaviour, not
a regression test for the new filter, and it does not exercise the `when` clause at
`Notifications.cs:425`. The brief's "caller-cancel genuinely fails on the prior behaviour"
expectation is not met by this test.

This is not a coverage hole for the distinction itself: the pre-existing test at `:531-551`
throws a host `TaskCanceledException` after the caller token is canceled, which is exactly the
filter's false branch and would fail if the filter were removed. If the intent is a
pre-change-failing test, make the host throw
`new OperationCanceledException(cancellationToken)` **after** calling `source.Cancel()` and
assert the throw with no failure aggregation; that also pins the "only the caller's token
aborts" wording directly. No product change needed.

### P4 — record XML doc does not describe the new null case

**Location:** `src/Playnite.Core/Hosting/Notifications.cs:114-118` and the summary at `:125-129`.

`NotificationDismissFailure.Status` is documented as "the status the host reported, or
`Failed` when it threw", and the aggregate summary as "whether it returned an unsuccessful
result or threw". A null result is now recorded as `Failed` with `Threw: false` without either
condition holding. The unit page (`docs/port/p6-006-notifications-dialogs-mainview.md:45-52`)
was updated; the XML doc was not. Documentation only.

### Race note (no action)

`Notifications.cs:425` samples `IsCancellationRequested` when the filter runs, after the host
threw. If the caller cancels between the host throw and the filter evaluation, a host OCE is
treated as caller cancellation and rethrown; that matches the documented rule that caller
cancellation aborts the remaining messages, and the per-message failure has nowhere to be
reported because the call throws. No change requested.

## Merge position

No blocking P1/P2 finding in `f339b37..9dd1fe6`. The documentation states the behaviour the
code implements, the tests exercise the aggregate/continue paths, and the two new failure
classes are both in `Failures` and in the aggregate diagnostic. Merge authority remains with
the captain.
