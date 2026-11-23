using CK.Core;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;

namespace CK.ConfigurationModel.Tests;

/// <summary>
/// The passkeys of a party (the shape of the Passkey option).
/// </summary>
[ConfigurationModel]
public sealed class PasskeyModel : IConfigurationModel
{
    /// <summary>
    /// Gets the relying party identifier: a domain name (<c>example.com</c> or <c>localhost</c>).
    /// </summary>
    public required string RpId { get; init; }

    /// <summary>
    /// Gets the displayed name of the relying party. Defaults to the <see cref="RpId"/>.
    /// </summary>
    public string? RpName { get; init; }

    /// <summary>
    /// Gets the timeout of a ceremony.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes( 5 );

    /// <summary>
    /// The effective name: a derived member, not a key.
    /// </summary>
    public string EffectiveRpName => RpName ?? RpId.ToLowerInvariant();

    public void Validate( UserMessageCollector collector, string path )
    {
        if( Uri.CheckHostName( RpId.Trim() ) != UriHostNameType.Dns ) collector.Error( $"'{path}:RpId' = '{RpId}': a domain name is expected." );
        if( Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes( 10 ) ) collector.Error( $"'{path}:Timeout': at most 10 minutes." );
    }
}

public enum Mode
{
    Off,
    Lax,
    Strict
}

/// <summary>
/// A scheme of a provider.
/// </summary>
[ConfigurationModel]
public sealed class SchemeModel
{
    public required Uri Authority { get; init; }

    public required string ClientId { get; init; }

    [ConfigurationSecret]
    public string? ClientSecret { get; init; }
}

/// <summary>
/// The root model of the tests.
/// </summary>
[ConfigurationModel]
public sealed class RootModel
{
    [ConfigurationLevel( ConfigurationLevel.Run )]
    public int Count { get; init; } = 3;

    public Mode Mode { get; init; }

    public IReadOnlyList<string> Cultures { get; init; } = [];

    public PasskeyModel? Passkey { get; init; }

    public IReadOnlyDictionary<string, SchemeModel> Schemes { get; init; } = new Dictionary<string, SchemeModel>();

    [ConfigurationPerDeployment]
    public string? PublicUrl { get; init; }

    [ConfigurationSchema( "https://json.schemastore.org/appsettings.json#/definitions/kestrel" )]
    public IConfiguration? Kestrel { get; init; }
}

/// <summary>
/// A property type that is not supported.
/// </summary>
[ConfigurationModel]
public sealed class UnsupportedModel
{
    public System.IO.Stream? Stream { get; init; }
}
