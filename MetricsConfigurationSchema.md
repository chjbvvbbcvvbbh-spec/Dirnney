# Metrics configuration schema proposal

## Background and motivation
`MetricsBuilderConfigurationExtensions.AddConfiguration(IMetricsBuilder, IConfiguration)` is already documented as an API, but the configuration key schema is not written down in one place. This document captures the schema currently implemented by `MetricsConfigureOptions` and the metrics rule-selection logic so reviewers can evaluate the feature behavior.

## API in scope
```csharp
public static IMetricsBuilder AddConfiguration(this IMetricsBuilder builder, IConfiguration configuration)
```

## Configuration schema

### Section names
- `EnabledMetrics` is the scope-less/default-scope form and applies to both global and local meters.
- `EnabledGlobalMetrics` applies only to global meters.
- `EnabledLocalMetrics` applies only to local meters.

The same section names can appear under a listener bucket:
- `{ListenerName}:EnabledMetrics`
- `{ListenerName}:EnabledGlobalMetrics`
- `{ListenerName}:EnabledLocalMetrics`

### Entries within a section
Within any of the sections above, the supported entry shapes are:
- `Default = {value}`
- `{MeterName} = {value}`
- `{MeterName}:Default = {value}`
- `{MeterName}:{InstrumentName} = {value}`

Those entries mean:
- `Default` applies to all meters and all instruments.
- `{MeterName}` applies to all instruments from the named meter.
- `{MeterName}:Default` applies to all instruments from the named meter.
- `{MeterName}:{InstrumentName}` applies only to the named instrument within the named meter.
- `ListenerName` is matched against `IMetricsListener.Name`, and listener names are only supported as the outer section bucket, for example `{ListenerName}:EnabledMetrics:...`.
- `MeterName` uses case-insensitive exact match or a single `*` wildcard pattern.
- `InstrumentName` uses case-insensitive exact match. `Default` is a special instrument key that applies to all instruments in the selected meter.
- Section names and the `Default` key are matched case-insensitively.

### Rule selection semantics
- Metrics does not combine root-level rules and listener-specific rules as separate filters.
- For a given instrument, metrics selects the single most specific matching rule.
- Listener-specific rules are more specific than root-level rules.
- A more specific meter name wins over a less specific meter name; for wildcard patterns, longer patterns are considered more specific.
- An instrument-specific rule wins over a meter-wide rule.
- For local meters, `EnabledLocalMetrics` is more specific than `EnabledMetrics`; for global meters, `EnabledGlobalMetrics` is more specific than `EnabledMetrics`.

### Value semantics
- The effective setting is enabled/disabled only: `true` enables and `false` disables.
- Values that cannot be parsed as `bool` are ignored.

## Example
```json
{
  "EnabledMetrics": {
    "Default": "false",
    "Contoso.Legacy.*": "true",
    "Contoso.Service": {
      "request.duration": "true",
      "error.count": "false",
      "Default": "true"
    }
  },
  "EnabledGlobalMetrics": {
    "Contoso.ScopeSource": {
      "Default": "true"
    }
  },
  "EnabledLocalMetrics": {
    "Contoso.LocalOnly": "true"
  },
  "MyListener": {
    "EnabledMetrics": {
      "Default": "true"
    },
    "EnabledLocalMetrics": {
      "Contoso.Service": {
        "error.count": "false"
      }
    }
  }
}
```

## Notes
- Unknown top-level sections are treated as listener names, and only their `EnabledMetrics` / `EnabledGlobalMetrics` / `EnabledLocalMetrics` children are processed.
- Nested listener objects under a meter or instrument are not supported. For example, `EnabledMetrics:MyMeter:MyInstrument:MyListener=true` is parsed as an instrument name of `MyInstrument:MyListener`, not as a listener-specific override.
- Configuration entries that cannot be parsed as `bool` are ignored.
- Meter name patterns may contain at most one `*`. More than one wildcard is invalid and throws when metrics evaluates the rule.