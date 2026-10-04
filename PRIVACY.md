# Privacy

AuthSurface keeps endpoint routes, URLs, policy names, roles, schemes, requirement identities, controller/action names, application/service/repository names, filesystem paths, test names, claims, credentials, baseline contents, and diagnostics local to the process. None of those values is passed to telemetry.

AuthSurface requests the shared client's activation event only after a nonempty runtime endpoint report is evaluated by policy verification, baseline creation, or baseline comparison. The call takes no product data, and AuthSurface does not request recurring heartbeats. The shared client owns payload fields, opt-out handling, identity, local storage, delivery, and retention; see [KeelMatrix.Telemetry's privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for those maintained details.

The shared client provides best-effort, non-blocking delivery, and its failures cannot change AuthSurface scan or comparison results. AuthSurface keeps no separate telemetry state and has no hosted component; `authsurface.json` is local source-controlled state and may reveal application architecture.

The `authsurface.json` file itself may reveal internal architecture. Store it with the same care as other security-relevant application metadata.
