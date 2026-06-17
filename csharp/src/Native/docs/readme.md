# Apache Arrow ADBC Native Snowflake Driver for C#

This package provides a native C# implementation of the Apache Arrow ADBC (Arrow Database Connectivity) driver for Snowflake databases. Unlike the existing Interop driver that wraps the Go implementation, this driver is implemented entirely in C# and provides direct connectivity to Snowflake while returning results in Apache Arrow columnar format.

## Key Features

- **Native C# Implementation**: Direct C# implementation without Go interop dependencies
- **Arrow Format Support**: Leverages Snowflake's native Arrow support through their SQL REST API
- **ADBC Compliance**: Full adherence to ADBC API standards and conventions
- **Multiple Authentication Methods**: Support for username/password, RSA key pairs, OAuth 2.0, and SSO
- **Performance Optimized**: Connection pooling, streaming, and efficient memory management
- **Comprehensive Type Support**: Handles all Snowflake data types including semi-structured data

## Installation

```bash
dotnet add package Apache.Arrow.Adbc.Drivers.Snowflake
```

## Quick Start

```csharp
using Apache.Arrow.Adbc.Drivers.Snowflake;

// Create driver and database
var driver = new SnowflakeDriver();
var database = driver.Open("adbc.snowflake.sql.account=myaccount;username=myuser;password=mypassword;adbc.snowflake.sql.db=mydb");

// Connect and execute query
using var connection = database.Connect();
using var statement = connection.CreateStatement();
statement.SetSqlQuery("SELECT * FROM my_table LIMIT 10");

// Execute and get results in Arrow format
using var result = statement.ExecuteQuery();
while (result.MoveNext())
{
    var batch = result.Current;
    // Process Arrow RecordBatch
}
```

## Configuration

The driver supports ADBC-standard configuration options through connection strings following the specification at https://arrow.apache.org/adbc/main/driver/snowflake.html:

### Connection Parameters
- `adbc.snowflake.sql.account`: Snowflake account identifier (required)
- `username`: Username for authentication (required)
- `password`: Password for basic authentication
- `adbc.snowflake.sql.db`: Default database name
- `adbc.snowflake.sql.schema`: Default schema name
- `adbc.snowflake.sql.warehouse`: Compute warehouse to use
- `adbc.snowflake.sql.role`: Role to assume after connection
- `adbc.snowflake.sql.uri.host`: Snowflake host (optional, for custom endpoints)

### Authentication Parameters
- `adbc.snowflake.sql.auth_type`: Authentication method (snowflake, jwt, oauth, externalbrowser)
- `adbc.snowflake.sql.client_option.jwt_private_key_pkcs8_value`: RSA private key in PKCS8 format (for JWT auth)
- `adbc.snowflake.sql.client_option.jwt_private_key_pkcs8_password`: Passphrase for encrypted private key
- `adbc.snowflake.sql.client_option.auth_token`: OAuth access token

### Other Parameters
- `connection_timeout`: Connection timeout in seconds
- `enable_compression`: Enable gzip compression for requests

## Authentication Methods

### Username/Password
```csharp
var connectionString = "adbc.snowflake.sql.account=myaccount;username=myuser;password=mypassword";
```

### JWT (Key Pair)
```csharp
var connectionString = "adbc.snowflake.sql.account=myaccount;username=myuser;adbc.snowflake.sql.auth_type=jwt;adbc.snowflake.sql.client_option.jwt_private_key_pkcs8_value=YOUR_PRIVATE_KEY";
```

### OAuth 2.0
```csharp
var connectionString = "adbc.snowflake.sql.account=myaccount;username=myuser;adbc.snowflake.sql.auth_type=oauth;adbc.snowflake.sql.client_option.auth_token=your_token";
```

### External Browser
```csharp
var connectionString = "adbc.snowflake.sql.account=myaccount;username=myuser;adbc.snowflake.sql.auth_type=externalbrowser";
```

## Requirements

- .NET 8.0+
- Apache Arrow C# library
- Network connectivity to Snowflake

## License

Licensed under the Apache License, Version 2.0.