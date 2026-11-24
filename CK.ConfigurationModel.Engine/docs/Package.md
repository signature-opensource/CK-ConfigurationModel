The engine side of the configuration models: generates, in the code of an engine, the description of a model and its
creation, with no reflection at run time.

`ConfigurationModelCodeWriter` generates them for a model of CK.ConfigurationModel; the creation uses `[UnsafeAccessor]`
on its constructor and its `init` setters.

See the [README](https://github.com/signature-opensource/CK-ConfigurationModel/blob/stable/CK.ConfigurationModel.Engine/README.md).
