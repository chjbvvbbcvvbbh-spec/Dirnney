# Tracing configuration schema proposal

## Background and motivation
`TracingBuilderConfigurationExtensions.AddConfiguration(ITracingBuilder, IConfiguration)` is already documented as an API, but the configuration key schema is not written down in one place. This document captures the schema currently implemented by `TracingConfigureOptions` so reviewers can evaluate the feature behavior.

## API in scope
```csharp
public static ITracingBuilder AddConfiguration(this ITracingBuilder builder, IConfiguration configuration)
```

## Configuration schema

Tracing reuses the section layout described in [MetricsConfigurationSchema.md](MetricsConfigurationSchema.md), with tracing-specific names and semantics.

### Section names
- `EnabledTracing` is the scope-less/default-scope form and applies to both global and local activity sources.
- `EnabledGlobalTracing` applies only to global activity sources.
- `EnabledLocalTracing` applies only to local activity sources.

The same section names can appear under a listener bucket:
- `{ListenerName}:EnabledTracing`
- `{ListenerName}:EnabledGlobalTracing`
- `{ListenerName}:EnabledLocalTracing`

### Differences from metrics
- Tracing stops at the `ActivitySource.Name` level. There is no instrument-level key below an activity source.
- Within a tracing section, the supported entry shapes are `Default = {value}` and `{ActivitySourceName} = {value}`.
- `{ActivitySourceName}` applies to the named activity source.
- Nested child keys below `{ActivitySourceName}` are not interpreted as separate tracing rules.
- `ActivitySourceName` uses the same name-matching semantics as meter names in metrics: case-insensitive exact match or a single `*` wildcard pattern.

### Rule selection semantics
- `Default` applies to all activity sources.
- `ListenerName` is matched against `IActivityListener.Name`.
- Root-level rules and listener-specific rules are evaluated separately. A listener only receives events when both passes resolve to an enabled rule, so listener-specific rules act as an additional filter instead of overriding root-level rules.
- Section names and the `Default` key are matched case-insensitively.

### Value semantics
- The effective setting is enabled/disabled only: `true` enables and `false` disables.
- Only direct values on `Default` or `{ActivitySourceName}` are recognized.

## Example
```json
{
  "EnabledTracing": {
    "Default": "false",
    "Contoso.Service": "true",
    "Contoso.Legacy.*": false
  },
  "EnabledGlobalTracing": {
    "Contoso.ScopeSource": "true"
  },
  "EnabledLocalTracing": {
    "Contoso.LocalOnly": "true"
  },
  "MyListener": {
    "EnabledTracing": {
      "Default": "true"
    },
    "EnabledLocalTracing": {
      "Contoso.Service": "false"
    }
  }
}
```

## Notes
- Unknown top-level sections are treated as listener names, and only their `EnabledTracing` / `EnabledGlobalTracing` / `EnabledLocalTracing` children are processed.
- Nested listener objects or nested `Default` objects under an activity source are not supported. For example, neither `EnabledTracing:MySource:MyListener=true` nor `EnabledTracing:MySource:Default=true` is recognized as a tracing rule.
- Configuration entries that cannot be parsed as `bool` are ignored.
- Name patterns may contain at most one `*`. More than one wildcard is invalid and throws when tracing evaluates the rule.
