# SQL Server Simulator for .NET

Provides in-memory SQL Server emulation, intended for high performance parallel unit testing of .NET applications.

## Capabilities

* Not much yet; working toward supporting the basic functionality of Entity Framework Core 6.

## Limitations

* Doesn't support most SQL Server features, probably won't work for you in its current state. Feature parity is an ongoing challenge.
* Can't be used to check invalid syntax, since it assumes the source query is doing something not yet supported if it runs into trouble parsing.
