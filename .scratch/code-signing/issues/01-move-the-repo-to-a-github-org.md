# 01: Move the repo to a GitHub org and set up signing accounts

**What to build:** The repo lives under the Sprocket GitHub org, and the org holds the Azure Trusted Signing and Apple Developer accounts that signing binds to.

**Blocked by:** None (can start immediately)

**Status:** deferred — no paid Microsoft/Apple signing accounts for now; the initial release ships unsigned (decided 2026-09-30). Revisit in a future, unscheduled phase.

- [x] Repo transferred to the org; release workflow and update feed URLs still work — verified 2026-09-30: origin is `SprocketVideo/Sprocket`; `UpdateService.RepoUrl`, `release.ps1` `$VpkRepoUrl`, README badges all point at the org; release runs v0.1.100–102-alpha succeeded from the org
- [ ] Azure Trusted Signing (or chosen cert) account and Apple Developer Program account exist for the org
- [ ] Credentials stored as GitHub Actions secrets / federated credentials
