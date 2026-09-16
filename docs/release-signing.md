# Windows release signing

The tag-triggered Release workflow signs the published `BotOrNot.exe` with Azure Artifact Signing before creating `BotOrNot-Windows-x64.zip`. Signing or verification failure stops packaging and release creation; there is no unsigned-release fallback. Ordinary PR/build artifacts are not release-signed.

## Azure and GitHub configuration

This uses the same Azure account and verified publisher as MPD Bot, with a separate BotOrNot certificate profile and application identity:

- Azure account/resource group: `MPD-Artifacts`, region `westus2`.
- Certificate profile: `botornot-public`, type `PublicTrust`, publisher `Kenneth Caruso`.
- Entra application: `botornot-github-signing`. Its service principal receives only **Artifact Signing Certificate Profile Signer** on the `botornot-public` profile.
- GitHub environment: `artifact-signing` in `kc2-io/BotOrNot`. Custom deployment policy allows **tags** matching `v*`, not branches.
- Federated identity issuer: `https://token.actions.githubusercontent.com`; audience: `api://AzureADTokenExchange`; subject: `repo:kc2-io/BotOrNot:environment:artifact-signing`.

The workflow authenticates through GitHub OIDC and `azure/login`. There is no client secret or exported signing key. Keep the tag-only environment policy: the environment name is part of the OIDC subject. Environment access alone does not restrict which commit a maintainer can tag; tag publication remains a release-authority action.

The environment must contain these non-secret Actions variables:

| Variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | Application/client ID of `botornot-github-signing` |
| `AZURE_TENANT_ID` | Tenant containing the application and signing account |
| `AZURE_SUBSCRIPTION_ID` | Subscription containing `MPD-Artifacts` |
| `AZURE_SIGNING_ENDPOINT` | `https://wus2.codesigning.azure.net/` |
| `AZURE_SIGNING_ACCOUNT` | `MPD-Artifacts` |
| `AZURE_SIGNING_PROFILE` | `botornot-public` |
| `AZURE_SIGNING_PUBLISHER` | `Kenneth Caruso` |

The Azure login and signing actions are pinned to reviewed commit SHAs. Update the pins deliberately when upgrading their versions. MPD Bot's application, profile, and environment are independent and need no changes.

## Verification and release procedure

`scripts/test-release-signature.ps1` checks the verification gate with simulated Windows signature results (valid, missing file, unsigned, tampered, untrusted, missing certificates, and wrong publisher). Build CI runs these checks without signing access; they do not validate Azure connectivity or replace a real signature check.

Use the existing `v*` tag release process after merging the workflow. A first tag run is required to validate GitHub OIDC and Azure signing end to end; PR CI does not exercise the tag-only signing environment. Do not move an existing release tag just to adopt signing.

The action uses SHA-256 and Microsoft's RFC 3161 timestamp endpoint. `scripts/verify-release-signature.ps1` then requires Windows to report a valid Authenticode signature, a timestamp certificate, and the expected publisher. It checks the publisher name rather than a rotating short-lived certificate thumbprint. The verified file is subsequently included in the ZIP; the ZIP itself is not Authenticode-signed.

To check a downloaded release after extracting it on Windows:

```powershell
./scripts/verify-release-signature.ps1 -FilePath ./BotOrNot.exe -ExpectedPublisher 'Kenneth Caruso'
```

Code signing identifies the publisher and detects modification; it does not guarantee immediate SmartScreen reputation. The certificate identity is the verified publisher, not the product name.

References: [Azure signing action](https://github.com/Azure/artifact-signing-action), [OIDC setup](https://github.com/Azure/artifact-signing-action/blob/main/docs/OIDC.md), [Artifact Signing roles](https://learn.microsoft.com/en-us/azure/artifact-signing/concept-resources-roles).
