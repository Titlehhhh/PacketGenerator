# PacketGenerator Architecture

## Solution overview
The repository is organized as a multi-project .NET solution that mixes C# libraries and an F# console tool:
- **Protodef** (`src/Protodef`) is a C# library that models the protocol definitions from the upstream `minecraft-data` project. It exposes strongly typed representations for protocol namespaces, container fields, switches, primitive and custom types, plus helpers for enumeration and JSON serialization.
- **MinecraftData** (`src/MinecraftData`) provides small utilities for locating the upstream data files. `DataPathsHelper` reads `dataPaths.json` to map Minecraft versions to protocol folders, while `MinecraftPaths` centralizes the root data directory configuration.
- **PacketGenerator.Core** (`src/PacketGenerator.Core`) supplies the cross-language infrastructure used by the generator. Key services include `ProtocolLoader` for deserializing `minecraft-data` protocol JSON into `ProtodefProtocol` objects, `ProtocolValidator` for checking packet maps inside each namespace, and `ProtocolMap` for tracking the loaded versions and their file paths. `ArtifactsPathHelper` defines the output directory used by the generator.
- **PacketGenerator** (`src/PacketGenerator`) is the F# console application that orchestrates the generation workflow. It pulls protocol versions via `ProtocolLoader`, analyzes type histories with `HistoryBuilder`, and produces artifacts that describe how packet/type structures evolve across versions. It also emits C# classes for “primitive” packet structures using the code generation helpers in `CodeGeneration/`.
- Additional projects such as **MinecraftDataFSharp**, **SandBoxApp**, and **SandBoxLib** provide supporting utilities and experiments; they are not required for the core generation flow but live alongside the main components in the solution.

## Generation workflow
1. **Load protocol metadata** – `ProtocolLoader.LoadProtocolsAsync` reads the available version descriptors from `minecraft-data`, filters them by configured protocol version range, and deserializes each `protocol.json` file into a `ProtodefProtocol` instance. A `ProtocolMap` captures both the numeric protocol version and the associated Minecraft release identifiers for downstream lookups.
2. **Validate protocol consistency** – `ProtocolValidator` walks each loaded namespace to ensure that the protocol’s packet IDs and switch mappings are self-consistent. Validation errors are surfaced early with the file path context to help triage malformed input data.
3. **Analyze type evolution** – The generator enumerates all packet and auxiliary types exposed by the loaded protocols. For each type path, `HistoryBuilder` folds the sequence of definitions into a `TypeStructureHistory`, merging adjacent versions with identical structures so the history is expressed as version ranges instead of per-version entries.
4. **Write artifacts** – Artifacts are written under the path defined in `ArtifactsPathHelper`. The generator creates per-type JSON files that include the namespace path, type name, and the serialized history of structures across versions, grouping packet artifacts by state and side folders (e.g., `play/toClient`).
5. **Generate code for primitive packets** – For packet histories whose structures remain simple (e.g., non-nested containers or primitive types), the generator converts the history into a specification and invokes the `ClassGenerator` to emit C# classes. Generated files are stored under the `codeGen` artifact folder with PascalCase filenames derived from the protocol paths.

## Key data structures
- **ProtocolMap** holds the loaded protocols keyed by version, tracks the mapping from Minecraft version strings to protocol numbers, and exposes convenience helpers (e.g., `AllTypesPath()` from the `Protodef` library) that the generator consumes when scanning types.
- **TypeStructureHistory** (F#) models how a protocol type changes over time. Each entry combines a `VersionRange` with an optional `ProtodefType`, allowing the history to represent both present and absent definitions across protocol versions.
- **Artifacts layout** is cleared and recreated on each generator run. The `diff/` subtree contains the per-type history JSON files, while `codeGen/` holds any generated C# packet classes.

## Execution entry point
The primary entry point is `src/PacketGenerator/Program.fs`. The program wires together the services above: it loads protocol versions (currently hard-coded to 735–772), builds and writes histories for all packets and auxiliary types, runs primitive-packet code generation, emits summary logs, and exits once all artifacts have been produced.
