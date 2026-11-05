# Current changes — Gallery

- Collection response now includes accessible category names; media requests can filter by category. Deployed to the live Gallery IIS installation. Authenticated category response remains unverified because Open Wallpaper's stored OIDC credential failed its identity match check.

- Recovery errors now remain attached to individual file operations while the worker continues. Administrators can hold and resume an operation in the review dialog. Live recovery completion and the missing files in operation 12719 remain to be reviewed.
- Administrators can find an operation by ID and resolve a superseded duplicate operation after the server verifies the surviving bytes and references. Other error types still need distinct safe repair paths.

- Corrected invisible resolution buttons and absent-path validation. Review now presents plain-language resolution choices and image previews, with technical details collapsed. Deployed at 11:54 Denver time; synthetic interactions and compiled path checks passed.

- Full recovery decisions are deployed: keep-first/second/both, retry, move completion/cancellation, backup restoration and locating a missing copy. The worker applies choices and reports results; the panel clears completed issues. Deployed at 12:08 Denver time, without schema or storage permission changes.
