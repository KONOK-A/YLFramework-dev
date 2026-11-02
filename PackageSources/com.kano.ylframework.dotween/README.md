# YLFramework DOTween Integration

Install `com.kano.ylframework` and `com.kano.ylframework.dotween` at the same
validated `0.1.0-preview.1` revision. For Git installs, declare both Git URLs
explicitly in the project manifest.

Adapter URL format (`<full-commit-sha>` is notation, not a usable revision):

```text
https://github.com/KONOK-A/YLFramework-dev.git?path=/PackageSources/com.kano.ylframework.dotween#<full-commit-sha>
```

Use the actual full SHA and main-plus-adapter manifest entries supplied by the
acceptance handoff, not a guessed tag or mutable branch. See the
[installation guide](../com.kano.ylframework/Documentation~/README.md#git-url-与依赖闭包).

This package contains YL Action adapters only. Install the official DOTween
Standard 1.2.825 SDK separately and complete its setup, including the `DOTWEEN`
define. DOTween and DOTween Pro are not bundled or downloaded by this adapter.
This is a product packaging boundary: the Standard vendor license permits
verbatim source redistribution with the original notices, disclaimers and
readme.txt, optionally accompanied by compiled form. Modified versions and Pro
have separate restrictions; the internal `redistributionAuthorized: false` flag
does not mean the vendor forbids all unchanged Standard redistribution.
Consumers with their own asmdef must reference `YLFramework.Actions.DOTween`.

SDK licensing and provenance are described in [Dependencies.json](Dependencies.json)
and [LICENSE.md](LICENSE.md); the adapter
does not grant or change the SDK license. Runtime assembly and namespace names
remain unchanged.
Package updates and removal do not replace or delete the separately installed
SDK or project-owned data. Maintained by Kano; original copyrights are retained.
