# amicus-api — design decisions log

Why the system is the way it is: the incident, the evidence, the decision, how it was verified.
The README holds the resulting *rules* and cites entries as **(Dn)**. Read an entry when a rule cites
it, when you are about to change the thing it covers, or when tracing why.

Find one: `grep -n '^## D1 ' docs/DECISIONS.md`

## Adding an entry

- Append at the END: `## D<next> — <title> (YYYY-MM-DD)`, then the body — what happened (with the
  evidence), the decision, what was verified live.
- `<next>` = highest number + 1: `grep -oE '^## D[0-9]+' docs/DECISIONS.md | tr -d '#D ' | sort -n | tail -1`.
- Never renumber and never rewrite an old entry. If it turns out wrong, append a paragraph to it
  starting `**Correction (YYYY-MM-DD):**`.
- In the same commit, add or update the one-line rule in the README with `(D<next>)`.

---

## D1 — The Pi runner and the API services run as an unprivileged user (2026-10-14)

**What happened.** The deploy pipeline (PR #11, 2026-09-15) registered the self-hosted runner
`pi-amicus` as the Pi's own user `thorsp`, and both API services ran as `thorsp` too. That user has
passwordless sudo for everything and holds every credential on the Pi and the SSH keys to the
operator's other machines. The repo is public, the runner group allowed public repositories, and fork
PRs only needed approval from first-time contributors. So a fork PR from anyone with one earlier
merged PR could add its own workflow with `runs-on: [self-hosted, pi]` and run as root on the Pi. The
`if: github.repository == …` guard in `deploy.yml` only stops a fork running *that* file. Raised in
`#security-alerts` on 2026-10-13 by a session designing another project's deploys; nothing had
exploited it (only the operator and Marc contribute; no fork PRs exist).

Evidence: `gh api repos/amicusTimisoara/amicus-api/actions/permissions/fork-pr-contributor-approval`
→ `first_time_contributors`; `gh api orgs/amicusTimisoara/actions/runner-groups` → Default,
`allows_public_repositories: true`; the runner unit had `User=thorsp`; `sudo -l -U thorsp` →
`(ALL) NOPASSWD: ALL`.

**Decision.**
- Fork PRs need approval for every outside contributor, on all three repos
  (`approval_policy: all_external_contributors`).
- A system user `amicus` (home `/srv/amicus`, no login shell, not in `docker`) owns the published
  apps (`/srv/amicus/apps/…`), the env files and Data Protection keys (`/srv/amicus/.config/amicus/`)
  and the runner (`/srv/amicus/actions-runner`). The .NET SDK it uses is a root-owned copy at
  `/opt/dotnet` (same 10.0.201 SDK).
- Both services run as `amicus` with `ProtectHome=true` (they no longer see `/home` at all).
- The runner's unit has a drop-in (`hardening.conf`): `ProtectHome=true`, `PrivateTmp=true`,
  `ProtectSystem=full`. Its sudo (`/etc/sudoers.d/amicus-deploy`) is only `systemctl restart`,
  `is-active` and `--no-pager status` for the two services.
- The layout keeps the same paths relative to `$HOME`, so `deploy.sh` is unchanged and the release
  tags' own copies still deploy.

Worst case now: a hostile workflow can break or read AMiCUS (its DB login, its keys), not the rest of
the Pi.

**Verified live.** As `amicus`: reading `~thorsp/.config/gh/hosts.yml` and `~thorsp/.ssh` → permission
denied; `docker ps` → denied; `sudo systemctl restart nginx` and `sudo cat /etc/shadow` → password
required; `sudo systemctl is-active amicus-api-stage.service` → works. Login tokens issued before the
switch still worked after it on both envs (Data Protection keys carried over). A dispatched stage
deploy (run 38467059511) and a re-run of the v0.3.0 release deploy (run 35433095539, attempt 2) both
published, migrated, restarted and passed the health check on the new runner.
