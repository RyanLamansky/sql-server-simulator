# Extended Events

The Extended Events engine's catalog, as SQL Server 2025 lists it.

## The engine's catalog

`sys.dm_xe_packages`, `sys.dm_xe_objects`, `sys.dm_xe_object_columns` and `sys.dm_xe_map_values` list real's own rows, byte for byte and in real's order: 18 packages, 3,864 objects, 32,264 object columns and 10,275 map values, dumped 2026-10-16 from SQL Server 2025 (17.0.4065.4) on Linux.
The rows live as tab-separated text under `src/SqlServerSimulator/ExtendedEvents/Metadata/`, one file per view, with each package and object referenced by its line number so a GUID is written once; the build zips the folder into the assembly (`ZipXeMetadata` in the project file), since the text is some 3 MB and the zip a sixth of that.
`XeMetadata` reads it once per process on first use.
A refreshed dump replaces the files wholesale: the format is in `XeMetadata`'s remarks.

`module_address` is real's load address of each module as the dump saw it, a stand-in like the other addresses the SQLOS views report.
All four views take the server-scope gate (Msg 300 then 297), as real's do (probed 2026-10-16).
