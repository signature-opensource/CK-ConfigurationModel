The configuration models: a `[ConfigurationModel]` partial class is the model of a configuration section (its properties
are the keys); its description is generated at compile time (`IDescribedConfigurationModel<TSelf>`),
`ConfigurationModelReader` reads it key by key along the levels of the configuration and collects the errors (fail
closed), `ConfigurationModelSchema` writes it as a JSON Schema.
See the [README](https://github.com/signature-opensource/CK-ConfigurationModel/blob/stable/CK.ConfigurationModel/README.md).
