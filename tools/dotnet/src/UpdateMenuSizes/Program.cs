using UpdateMenuSizes;

// Faithful C# port of scripts/update_menu_sizes.py (issue #16). With no arguments, discovers the
// one persona pack that has opted into this tool (personas/*/menu/source/*-menu-items.json with a
// sibling menu/product_search_map.json, via PersonaMenuLocator) and resolves its conventional
// sibling files, so `dotnet run --project tools/dotnet/src/UpdateMenuSizes` behaves like
// `python scripts/update_menu_sizes.py` for whichever persona has opted in today -- no persona id
// is hardcoded here. --production/--menu/--product-search-map let tests (and anyone who wants a
// dry run against a copy) point at different files without touching the real ones, and also skip
// persona discovery entirely (see CliRunner). All argument parsing, discovery, and top-level error
// handling lives in CliRunner.cs, which is unit-tested directly (UpdateMenuSizes.Tests) --
// Program.cs itself is kept to the thinnest possible entry point.
return CliRunner.Run(args, Console.Out, Console.Error);
