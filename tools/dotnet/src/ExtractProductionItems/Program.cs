using System.Text;
using ExtractProductionItems;

// Faithful C# port of scripts/extract_production_items.py (issue #16 batch 1). With no arguments,
// discovers the one persona pack that has a production export (personas/*/menu/source/*-menu-items.json,
// via ProductionExportLocator) and resolves its conventional sibling menuItems.json, so
// `dotnet run --project tools/dotnet/src/ExtractProductionItems` behaves like
// `python scripts/extract_production_items.py` for whichever persona pack exists today -- no
// persona id is hardcoded here. --production/--menu let tests (or a dry run against a copy) point
// at different files, and also skip persona discovery entirely (see CliRunner). All argument
// parsing, discovery, and top-level error handling lives in CliRunner.cs, which is unit-tested
// directly (ExtractProductionItems.Tests) -- Program.cs itself is kept to the thinnest possible
// entry point.
//
// The report includes box-drawing and emoji characters (matching the Python twin's own output
// verbatim); Windows' legacy console codepage mangles those by default (the Python twin hits the
// exact same failure without PYTHONIOENCODING=utf-8 set -- confirmed while writing this port), so
// this explicitly forces UTF-8 console output, which the Python twin does not do for itself.
Console.OutputEncoding = Encoding.UTF8;

return CliRunner.Run(args, Console.Out, Console.Error);
