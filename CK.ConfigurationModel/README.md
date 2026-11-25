# CK.ConfigurationModel

A configurable type (an aspect, a party feature, a client feature, an option) has a configuration section. Read by
hand, its keys are declared nowhere: the tools can't show them, type them or tell a misspelled one, and every owner
writes its own reader. This package makes the section a **model**: a class whose properties are the keys, read and
validated for its owner, and described for the tools (the App builder, the configurator) as a JSON Schema.

## A model

```csharp
[ConfigurationModel]
public sealed class PasskeyConfiguration : IConfigurationModel
{
    /// <summary>Gets the relying party identifier: a domain name.</summary>
    public required string RpId { get; init; }

    public string? RpName { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes( 5 );

    // A derived member (a normalized value, a computed default): not a key.
    public string EffectiveRpName => RpName ?? RpId.ToLowerInvariant();

    public void Validate( IConfigurationValidationContext context )
    {
        if( Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes( 10 ) ) context.Error( $"'{context.Path}:Timeout': at most 10 minutes." );
    }
}
```

- [`[ConfigurationModel]`](https://github.com/signature-opensource/CK-Abstractions/blob/stable/CK.AppBuilder.Abstractions/ConfigurationModel/ConfigurationModelAttribute.cs) marks the class (it has a parameterless constructor, public
  or not); the configurable type that owns it says so with `[Configuration<TModel>]` (CK.AppBuilder.Abstractions).
- **The keys** are the public instance properties with a setter or an `init` accessor declared by the class: a `required`
  one is required, a nullable one is optional, an initializer is the default. A get-only property is a derived member.
- **A property** is a basic value (string, bool, int, long, double, decimal, an enum by name, `TimeSpan`, `Guid`, `Uri`),
  a nested model, a list (an array, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IEnumerable<T>`) of values or models,
  a dictionary by name of models or of values (`IReadOnlyDictionary<string, T>`, `IDictionary`, `Dictionary`), or an opaque
  `IConfiguration` (its consumer reads it; `[ConfigurationSchema( uri )]` names the JSON Schema that describes it).
- **The metadata** are attributes of the properties (CK.AppBuilder.Abstractions): `[ConfigurationLevel]` (`Init` by default),
  `[ConfigurationSecret]`, `[ConfigurationPerDeployment]`.
- [`IConfigurationModel`](https://github.com/signature-opensource/CK-Abstractions/blob/stable/CK.AppBuilder.Abstractions/ConfigurationModel/IConfigurationModel.cs)
  (optional): `Validate( IConfigurationValidationContext )` (the `Path` of the section, `Error`, `Warn`) checks what the
  types can't say. It has no service: it runs wherever the model is read.

## The description

[`ConfigurationModelDescription`](ConfigurationModelDescription.cs) describes a model: its properties, their kind, types,
required, nullable, default value, metadata and the summary of their Xml documentation.

- **By reflection**, for the tools, the App builder, the engines and the tests:
  [`ConfigurationModelDescription.TryGet( Type )`](ConfigurationModelDescription.cs) builds it once per type, or says why
  the type is not a model (not a `[ConfigurationModel]`, an unsupported property type, no parameterless constructor).
  The documentation comes from the Xml documentation file next to the model's assembly, when there is one (the packages
  have it; a local Debug build doesn't).
- **As code**, at run time: the engine that creates a modeled object generates its description and its creation with
  [CK.ConfigurationModel.Engine](../CK.ConfigurationModel.Engine/README.md). The model is created without reflection: an
  `[UnsafeAccessor]` calls its constructor and the `init` setters of the read properties (the others keep their
  initializer). The `required` members are checked by the reader, never by a constructor.

A project that declares a model needs nothing but this package (no analyzer, no `partial`).

## Reading a model

[`ConfigurationModelReader.TryRead`](ConfigurationModelReader.cs) reads a model, by its description (`TryRead<T>`
describes `T` by reflection: the tools and the tests), from the **layers** of its section: the
section itself, then the sections of the levels above it that hold defaults (the `"Defaults"` of the AppIdentity
configuration), the nearest first.

- **Key by key:** each property is the value of the nearest layer that has its key; a nested model key by key from the
  layers of its key; a dictionary entry by entry (the union of the entries of the layers: a model key by key, a value
  from the nearest layer); a list as a whole. A list of
  values also accepts a comma separated string.
- **The reserved keys** (the options of the owner of the model, keys of the same section) are not unknown keys.
- **Every error is collected** in the `UserMessageCollector` (CK.Globalization): an unknown key in any layer, a missing
  required key, a value that can't be converted (the invariant culture: `"00:05:00"`, `"1.00:00:00"` for a `TimeSpan`),
  then `Validate`.
- **Fail closed:** when an error has been collected, there is no model.

Outside DI (at build time, in a tool), a collector is
`new UserMessageCollector( new CurrentCultureInfo( new TranslationService(), culture ) )`.

## The JSON Schema

[`ConfigurationModelSchema.ToJsonSchema`](ConfigurationModelSchema.cs) writes a description as a JSON Schema (2020-12):
an object per model (`required`, `additionalProperties: false`, `description`), the nested models in `$defs`, the lists
as arrays (a list of values also accepts a string), the dictionaries as objects of models or of values
(`additionalProperties`), an opaque property as a
`$ref` to its schema, the default value of a basic value as its `default`. The CK vocabulary (annotations): `x-ck-level` (when not `Init`), `x-ck-secret`,
`x-ck-perDeployment`, `x-ck-format` (`"timespan"` on a `TimeSpan` string: the .NET syntax `"00:05:00"`, not the ISO 8601
`duration` of JSON Schema).
