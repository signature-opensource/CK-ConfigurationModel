# CK.ConfigurationModel

A configurable type (an aspect, a party feature, a client feature, an option) has a configuration section. Read by
hand, its keys are declared nowhere: the tools can't show them, type them or tell a misspelled one, and every owner
writes its own reader. This package makes the section a **model**: a class whose properties are the keys, read and
validated for its owner, and described for the tools (the App builder, the configurator) as a JSON Schema.

## A model

```csharp
[ConfigurationModel]
public sealed partial class PasskeyConfiguration : IConfigurationModel
{
    /// <summary>Gets the relying party identifier: a domain name.</summary>
    public required string RpId { get; init; }

    public string? RpName { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes( 5 );

    // A derived member (a normalized value, a computed default): not a key.
    public string EffectiveRpName => RpName ?? RpId.ToLowerInvariant();

    public void Validate( UserMessageCollector collector, string path )
    {
        if( Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes( 10 ) ) collector.Error( $"'{path}:Timeout': at most 10 minutes." );
    }
}
```

- [`[ConfigurationModel]`](ConfigurationModelAttribute.cs) marks a **`partial`** class; the configurable type that owns
  it says so with `[Configuration<TModel>]` (CK.Abstractions).
- **The keys** are the public instance properties with a setter or an `init` accessor declared by the class: a `required`
  one is required, a nullable one is optional, an initializer is the default. A get-only property is a derived member.
- **A property** is a basic value (string, bool, int, long, double, decimal, an enum by name, `TimeSpan`, `Guid`, `Uri`),
  a nested model, a list (an array, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IEnumerable<T>`) of values or models,
  a dictionary of models by name (`IReadOnlyDictionary<string, TModel>`, `IDictionary`, `Dictionary`), or an opaque
  `IConfiguration` (its consumer reads it; `[ConfigurationSchema( uri )]` names the JSON Schema that describes it).
- **The metadata** are attributes of the properties (CK.Abstractions): `[ConfigurationLevel]` (`Init` by default),
  `[ConfigurationSecret]`, `[ConfigurationPerDeployment]`.
- [`IConfigurationModel`](IConfigurationModel.cs) (optional): `Validate( UserMessageCollector, path )` checks what the
  types can't say. It has no service: it runs wherever the model is read.

## The generated description

The source generator (CK.ConfigurationModel.Generator, shipped as an analyzer of this package) completes each model: it
implements [`IDescribedConfigurationModel<TSelf>`](IDescribedConfigurationModel.cs) (the static `ModelDescription`, a
[`ConfigurationModelDescription`](ConfigurationModelDescription.cs): the properties, their kind, types, required,
nullable, default, metadata and the summary of their Xml documentation) and a private constructor that sets the read
values (the others keep their initializer). The collections are created by generated code: nothing is read by
reflection. Its errors: `CKCM001` (a model must be `partial`), `CKCM002` (an unsupported property type), `CKCM003` (a
model must be a top level class).

## Reading a model

[`ConfigurationModelReader.TryRead<T>`](ConfigurationModelReader.cs) reads a model from the **layers** of its section: the
section itself, then the sections of the levels above it that hold defaults (the `"Defaults"` of the AppIdentity
configuration), the nearest first.

- **Key by key:** each property is the value of the nearest layer that has its key; a nested model key by key from the
  layers of its key; a dictionary entry by entry (the union of the entries of the layers); a list as a whole. A list of
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
as arrays (a list of values also accepts a string), the dictionaries as objects of models, an opaque property as a
`$ref` to its schema. The CK vocabulary (annotations): `x-ck-level` (when not `Init`), `x-ck-secret`,
`x-ck-perDeployment`.
