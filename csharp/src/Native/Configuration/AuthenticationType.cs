namespace AdbcDrivers.Snowflake.Native.Configuration;

/// <summary>
/// Represents the available authentication types for Snowflake.
/// </summary>
internal enum AuthenticationType
{
    /// <summary>
    /// Username and password authentication.
    /// </summary>
    UsernamePassword,

    /// <summary>
    /// RSA key pair authentication.
    /// </summary>
    KeyPair,

    /// <summary>
    /// OAuth 2.0 token authentication.
    /// </summary>
    OAuth,

    /// <summary>
    /// Single Sign-On authentication.
    /// </summary>
    Sso,

    /// <summary>
    /// External browser authentication.
    /// </summary>
    ExternalBrowser
}