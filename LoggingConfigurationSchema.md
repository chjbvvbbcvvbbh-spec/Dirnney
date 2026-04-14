# Logging configuration schema proposal

## Background and motivation
`LoggingBuilderExtensions.AddConfiguration(ILoggingBuilder, IConfiguration)` is already documented as an API, but the logging filter key schema is not written down in one place. This document captures the schema currently implemented by `LoggerFilterConfigureOptions` and `LoggerRuleSelector` so reviewers can evaluate the feature behavior.

## API in scope
```csharp
public static ILoggingBuilder AddConfiguration(this ILoggingBuilder builder, IConfiguration configuration)
```

## Configuration schema

This schema is relative to the `IConfiguration` section passed to `AddConfiguration`. In typical app configuration, that section is `Logging`.

### Root-level entries
- `CaptureScopes = {true|false}`
- `LogLevel`
- `{ProviderName}`

### Filter sections
The supported filter section shapes are:
- `LogLevel:{CategoryName|Default} = {LogLevel}`
- `{ProviderName}:LogLevel:{CategoryName|Default} = {LogLevel}`

Those entries mean:
- `CaptureScopes` sets `LoggerFilterOptions.CaptureScopes`.
- `LogLevel:Default` applies when no more specific category rule matches.
- `LogLevel:{CategoryName}` applies to all providers for matching categories.
- `{ProviderName}:LogLevel:Default` applies to the named provider when no more specific category rule matches for that provider.
- `{ProviderName}:LogLevel:{CategoryName}` applies only to the named provider and matching categories.

### Name semantics
- `ProviderName` can be either the provider type's full name or its `ProviderAliasAttribute` alias.
- Provider names are matched exactly; wildcards are not supported for provider names.
- `CategoryName` uses case-insensitive exact match or a single `*` wildcard pattern.
- `Default` is a special category key and means no category restriction.
- The `LogLevel` section name and the `Default` category key are matched case-insensitively.

### Value semantics
- Filter values must be valid `LogLevel` names, such as `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, or `None`.
- Empty or missing values are ignored.
- Invalid non-empty values are not ignored; they throw `InvalidOperationException` during configuration binding.
- `CaptureScopes` is parsed as a Boolean value and defaults to the existing `LoggerFilterOptions.CaptureScopes` value when omitted.

### Rule selection semantics
- If a provider-specific rule matches, it is preferred over rules without a provider.
- Among matching category rules, the longest matching category pattern wins.
- If no category-specific rule matches, a `Default` category rule is used if present.
- If multiple rules are equally specific, the last one wins.
- If no rule matches, logging falls back to `LoggerFilterOptions.MinLevel`; this schema does not configure `MinLevel` directly.

## Example
```json
{
  "CaptureScopes": "true",
  "LogLevel": {
    "Default": "Warning",
    "Microsoft": "Error",
    "MyApp.*": "Information"
  },
  "Console": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting": "Warning"
    }
  },
  "Microsoft.Extensions.Logging.Debug.DebugLoggerProvider": {
    "LogLevel": {
      "Default": "Trace"
    }
  }
}
```

## Notes
- Unknown top-level sections are treated as potential provider names, and only their `LogLevel` children are processed by the filter configuration binder.
- Category patterns may contain at most one `*`. More than one wildcard is invalid and throws when logging evaluates the rule.
- Provider aliases and full provider type names are both considered during rule selection.