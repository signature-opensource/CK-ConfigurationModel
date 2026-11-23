using System;

namespace CK.Core;

/// <summary>
/// Marks a configuration model: a <c>partial</c> class whose settable properties are the keys of a configuration section
/// (see <see cref="ConfigurationAttribute{TModel}"/> for the link from a configurable type to its model). The source
/// generator of CK.ConfigurationModel completes it: it implements <see cref="IDescribedConfigurationModel{TSelf}"/> (the
/// description of the model, read by <see cref="ConfigurationModelReader"/> and written as a JSON Schema by
/// <see cref="ConfigurationModelSchema"/>).
/// <para>
/// The keys are the public instance properties with a setter or an <c>init</c> accessor, declared by the class: a
/// <c>required</c> one is required, a nullable one is optional, an initializer is the default. A get-only property is a
/// derived member (a normalized value, a computed default), never a key. A property is a basic value (a string, a boolean,
/// a number, an enum by name, a <c>TimeSpan</c>, a <c>Guid</c>, an <c>Uri</c>), a nested model, a list (an array or a read
/// only list) of values or models, a dictionary of models by name (<c>IReadOnlyDictionary&lt;string, TModel&gt;</c>), or
/// an opaque <c>IConfiguration</c> (see <see cref="ConfigurationSchemaAttribute"/>).
/// </para>
/// </summary>
[AttributeUsage( AttributeTargets.Class, AllowMultiple = false, Inherited = false )]
public sealed class ConfigurationModelAttribute : Attribute
{
}
