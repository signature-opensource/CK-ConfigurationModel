namespace CK.Core;

/// <summary>
/// A configuration model completed by the source generator of CK.ConfigurationModel (see
/// <see cref="ConfigurationModelAttribute"/>): its description is a static member, so that a model is read without
/// reflection (<see cref="ConfigurationModelReader.TryRead{T}(System.Collections.Generic.IReadOnlyList{ImmutableConfigurationSection?}, string, UserMessageCollector, out T?)"/>).
/// </summary>
/// <typeparam name="TSelf">The model type.</typeparam>
public interface IDescribedConfigurationModel<TSelf> where TSelf : class, IDescribedConfigurationModel<TSelf>
{
    /// <summary>
    /// Gets the description of the model.
    /// </summary>
    static abstract ConfigurationModelDescription ModelDescription { get; }
}
