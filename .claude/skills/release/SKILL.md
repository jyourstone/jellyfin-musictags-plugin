---
name: release
description: Create and push a release tag for the MusicTags plugin with a changelog in the annotated tag message. Project-specific version scheme — RC number lives in the Revision segment with a bare -rc suffix (e.g. v12.0.1.1-rc); stable releases bump the Build segment and reset Revision to 0 (e.g. v12.0.2.0). Use when the user wants to tag a release or create an RC.
argument-hint: stable|rc
allowed-tools: Bash
---

# Release Skill

Create a new git version tag with a changelog generated from commits and PR titles since the last relevant tag, then push it to the remote. This plugin uses .NET's four-part `Major.Minor.Build.Revision` version scheme, not SemVer, and ships a single `12.x` release line tagged from `main`. Pushing the tag runs `.github/workflows/release.yml`, which builds the plugin, creates the GitHub release and updates the Jellyfin plugin manifest (stable → `main` manifest branch, RC → `unstable`).

## Steps

### 1. Validate input

`$ARGUMENTS` must be `stable` or `rc`. `patch` and `minor` are accepted as synonyms for `stable` — the four-part scheme doesn't distinguish bump sizes, so every stable is a Build bump regardless of how large the change is.

If `$ARGUMENTS` is `major`, stop and explain: the Major/Minor segments are pinned to the `12.x` line and only move as a deliberate manual decision, not something this skill infers from a bump type.

**If `$ARGUMENTS` is empty**, present this prompt and wait for the user's answer before proceeding:

```text
What type of release do you want to create?

  stable — Stable release (e.g. v12.0.1.0)
  rc     — Release candidate (e.g. v12.0.1.1-rc)
```

Use the user's answer as `$ARGUMENTS` and continue. If `$ARGUMENTS` is present but doesn't match `stable`, `patch`, `minor`, or `rc`, stop and tell the user: "Usage: /release stable|rc".

### 2. Sync and check working tree

First check the current branch:

```bash
git branch --show-current
```

Both `rc` and `stable` are tagged from `main`.

If the current branch is `10.11-release`, **hard stop** — no confirmation prompt. That branch exists only for a manual Jellyfin 10.11 hotfix (its workflow builds net9.0 / targetAbi 10.11.0), and its version numbers follow the `10.11.x` line, not the rules below:

```text
10.11-release is the Jellyfin 10.11 hotfix branch — this skill only releases the 12.x line.
Switch to 'main' and re-run, or tag a 10.11 hotfix manually.
```

For any other branch, warn and ask for confirmation before continuing:

```text
You are on branch '<current>' but releases are tagged from 'main'.
Continue anyway? (yes / no)
```

Run `git pull` to bring the local branch up to date, then `git fetch --tags --prune-tags --force` so the local tag list matches the remote (a plain `git pull` doesn't reliably sync new or deleted tags, and a stale tag list would corrupt the version calculation). If the pull fails, stop and show the error — do not proceed with a stale or diverged branch.

Then check for uncommitted changes:

```bash
git status --porcelain
```

If there are uncommitted changes, list them and stop: `Working tree is not clean — commit or stash first, then re-run /release.`

### 3. Gather context

Determine the changelog base tag depending on release type:

```bash
# rc: highest v12.* tag of any kind (stable or RC)
git tag --list 'v12.*' --sort=-v:refname | head -1

# stable: highest stable v12.*.0 tag — exclude RCs, since the changelog for a
# stable covers everything since the previous STABLE, not since the last RC.
# NOTE: this is the CHANGELOG base only. The VERSION base in step 4 is the
# highest v12.* tag of ANY kind (RCs included) — see the warning there.
git tag --list 'v12.*.0' --sort=-v:refname | grep -v -- '-rc$' | head -1
```

Then collect commits and PR titles since that base:

```bash
CHANGELOG_BASE="<tag from above, may be empty>"

# Commits since changelog base (or all commits if no previous tag)
if [ -z "$CHANGELOG_BASE" ]; then
  git log --oneline --no-merges
else
  git log ${CHANGELOG_BASE}..HEAD --oneline --no-merges
fi

# PR titles merged since changelog base (requires gh CLI — skip gracefully if unavailable)
# Note: %cI gives strict ISO 8601 with no spaces — required, because the default
# %ci format contains spaces that break the gh search query.
if command -v gh &>/dev/null; then
  CHANGELOG_BASE_DATE=$(git log -1 --format=%cI "${CHANGELOG_BASE}" 2>/dev/null)
  if [ -n "$CHANGELOG_BASE_DATE" ]; then
    gh pr list --state merged --search "merged:>${CHANGELOG_BASE_DATE}" --json number,title,mergedAt --limit 50
  fi
fi
```

If there are no commits since the changelog base, stop and tell the user: "Nothing to release."

### 4. Calculate next version

The version scheme is .NET's four-part `Major.Minor.Build.Revision`. Ordering is purely numeric, left to right — there are no pre-release labels baked into the .NET version itself.

- **Revision > 0** with a bare `-rc` git-tag suffix marks a release candidate; the Revision number *is* the RC number. The `-rc` suffix exists only as a workflow routing marker (it sends the build to the unstable manifest branch and marks the GitHub release as a prerelease) — the workflow strips it, so it is never part of the actual .NET version.
- **Never** produce `-rc.N`-style suffixes (e.g. `v12.0.0.1-rc.2`). Two such tags would strip to the identical manifest version `12.0.0.1` and break plugin auto-update ordering. Each RC increments the Revision segment itself.

**`rc`**: base = highest `v12.*` tag (from step 3).
- If the base is itself an RC (ends in `-rc`), increment Revision: `v12.0.1.2-rc` → `v12.0.1.3-rc`.
- If the base is a stable tag, start a new RC cycle: bump Build, set Revision to 1: `v12.0.0.0` → `v12.0.1.1-rc`.
- If there is no `v12.*` tag, ask the user for the starting version before proceeding — this skill has no basis to guess it.

**`stable`**: base = highest `v12.*` tag of **any** kind — RCs included.

> **Two different bases, do not confuse them.** The *changelog* base (step 3) is the last **stable** tag, so the release notes span everything since the previous stable. The *version* base here is the highest tag of **any** kind, including RCs. Using the changelog base to compute the version produces a version that sorts **below** the RCs it supersedes.

- Bump Build, reset Revision to 0: `v12.0.1.2-rc` → `v12.0.2.0`; `v12.0.0.0` → `v12.0.1.0`.
- Never put a hotfix counter in Revision — Revision is reserved exclusively for RC numbers.

Why any-kind: suppose the last stable is `v12.0.0.0` and the RC cycle since then produced `v12.0.1.1-rc` and `v12.0.1.2-rc`. Bumping Build from the last *stable* gives `v12.0.1.0`, whose manifest version `12.0.1.0` sorts **below** `12.0.1.2` — every RC user is already on a higher version and would never be offered the stable. Bumping from the highest tag of any kind gives `v12.0.2.0`, which sorts above the whole cycle and pulls RC users onto it.

**Sanity check before tagging** — the new version must sort above every existing `v12.*` tag:

```bash
printf '%s\n%s\n' "$(git tag --list 'v12.*' --sort=-v:refname | head -1 | sed 's/-rc$//')" "<new-version>" \
  | sort -V | tail -1
```

This must print `<new-version>`. If it prints the existing tag instead, the version is too low — stop and recompute.

If the new tag already exists (`git rev-parse -q --verify refs/tags/<new-version>`), stop and say so.

### 5. Generate changelog

Write a concise, human-readable changelog using the commits and PR titles collected in step 3.

Guidelines:
- Group changes into categories where it makes sense: **Features**, **Bug Fixes**, **Improvements** — but only include categories that have entries.
- Prefer PR titles over raw commit messages when both refer to the same change (PRs are usually better worded).
- Skip noise: merge commits, version bumps, "fix typo", "WIP", etc.
- **Only include application-facing changes**: omit commits that only touch repo infrastructure — `.github/` workflows, `.gitignore`, linter configs, docs-only changes (`README`, `dev/`), or other housekeeping that doesn't affect the running plugin.
- **For stable releases**: omit bug fixes that were fixing issues in the new features added during that same RC cycle. Those are internal RC iteration details, not user-facing changes. Only include the features, improvements, and unrelated bug fixes that matter to users upgrading from the previous stable release.
- Keep each line short and punchy — this is a tag message, not a novel.
- Use plain text, not markdown (git tag messages render as plain text).
- The changelog is shown to end users (GitHub release + Jellyfin plugin catalog), so keep the wording non-technical and focused on what changed for them.

Format:
```text
Features:
- ...

Bug Fixes:
- ...

Improvements:
- ...
```

Do not include a title line here — that's assembled separately in step 6.

### 6. Tag message format — critical

The release workflow reads the changelog with `git tag -l --format='%(contents:body)'`, which returns everything **after the first blank line** of the tag message. That body is published verbatim as both the GitHub release body and the Jellyfin plugin-manifest changelog entry.

This means the tag message must be structured as:

```text
Release <version>

<changelog body from step 5>
```

For RCs, use "Release candidate <version>" instead of "Release <version>" as the first line.

The first line and the blank line beneath it are stripped before publishing — so the changelog body from step 5 must stand alone and make sense without that first line. Do not fold the title into the body, and do not omit the blank line separator.

### 7. Show summary and ask for confirmation

Display to the user in your message:

```text
New tag:    <new-version>
Previous:   <changelog-base-tag> (or "none")
Commits:    <count> commit(s) since previous tag

Changelog:
-----------
<full tag message text, including "Release <version>" header>
-----------

Ready to create and push this tag? (yes / no / edit)
```

- **yes** → proceed to step 8
- **no** → abort, inform the user no tag was created
- **edit** → ask the user to provide the revised changelog, then re-show the summary and ask again

### 8. Create and push the tag

Run each command as its own Bash call — never chained with `;` or `&&`. Pushing the tag is the moment the release goes out, so it stays a separate call.

1. Write the tag message (step 6, verbatim) to a file in the scratchpad with the Write tool — `release-<new-version>.txt`. `-F` avoids shell-quoting accidents with multi-line messages.
2. Create the annotated tag:

   ```bash
   git tag -a <new-version> -F <path/to/message file>
   ```

3. Push the tag:

   ```bash
   git push origin <new-version>
   ```

Confirm success: "✓ Tagged and pushed <new-version>"

If any command fails, show the error output and stop — do not attempt to clean up automatically. If the tag was created locally but the push failed, tell the user the tag exists locally and can be removed with `git tag -d <new-version>` or pushed manually once the issue is resolved.
