# Legacy oracle harness

Windows-only console that calls **existing v10 public APIs** and writes raw output to stdout.
It is not a new export format and must not normalise bytes.

```
LegacyOracle manifest <extension.yaml>   # ExtensionManifest.FromFile -> v10 YAML serializer
LegacyOracle theme <theme.yaml>          # ThemeManifest -> v10 YAML serializer
LegacyOracle raw <file>                  # byte-for-byte copy
```

The Windows oracle workflow builds it and uploads the outputs; the Phase 1 comparator will
diff the new app's output against these bytes. Any change to this harness is an oracle change.
