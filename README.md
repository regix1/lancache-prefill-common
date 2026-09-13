# lancache-prefill-common

Common code shared across lancache-prefill projects.  Not intended for use outside of these projects.

## Repo Structure


|  |  |
| ---------|----------|
| `/dotnet` | Contains C# code that is shared by all of the prefills.  Code will only be added here when it is used by ALL of the prefills, otherwise it should live with the prefill it applies to. |
| `/lib` | Precompiled assemblies that contain customizations that aren't merged into their respective upstream repos.  Keeping assemblies here allows for customization without having to wait for pull requests to be approved and merged. |

## Concurrent prefill protocol

The shared .NET library defines the operation ownership, fair request budget, item
claims, and protocol-v2 envelope used by every prefill daemon. See
[docs/prefill-protocol.md](docs/prefill-protocol.md) for the compatibility,
configuration, cancellation, recovery, and release contracts.

CI validates the exact solution on Linux with restore, format verification, a
non-incremental warning-as-error build, and credential-free tests before any
publish job. Analyzer, formatting, and linker diagnostics are not suppressed;
an existing diagnostic must be resolved before the gated publish can proceed.
