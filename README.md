# CK-ConfigurationModel

The models of the configuration sections of the CKomposable types.

| Project | Description |
|---|---|
| [CK.ConfigurationModel](CK.ConfigurationModel/README.md) | A section is a model (its properties are the keys): its description (by reflection), the reader (key by key along the levels, the errors collected) and its JSON Schema. |
| [CK.ConfigurationModel.Engine](CK.ConfigurationModel.Engine/README.md) | The engine side: generates the description of a model and its creation without reflection (`[UnsafeAccessor]`) in the code of an engine. |
