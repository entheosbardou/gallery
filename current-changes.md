# Current changes — Gallery

- Collection response now includes accessible category names; media requests can filter by category. Deployed to the live Gallery IIS installation. Authenticated category response remains unverified because Open Wallpaper's stored OIDC credential failed its identity match check.

- Recovery errors now remain attached to individual file operations while the worker continues. Administrators can hold and resume an operation in the review dialog. Live recovery completion and the missing files in operation 12719 remain to be reviewed.
