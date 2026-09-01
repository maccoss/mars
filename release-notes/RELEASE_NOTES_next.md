# MARS vNEXT Release Notes

One-sentence summary of the release, written when there is something to summarise.

## New Features

## Bug Fixes

- **Two decisions in the .NET port specification were invisible on GitHub.** Section 2 of
  `docs/dotnet-port-spec.md` records the decisions the port was built on as a table. Two of its
  six rows - the mzML passthrough strategy, and that the Python implementation would not be
  removed until the acceptance gates passed - sat below a later subsection, separated from the
  table header by a heading, three paragraphs and a code block. Markdown ends a table at the
  first line that is not part of it, so GitHub rendered those two rows as literal
  pipe-delimited text at the foot of an unrelated section, and anyone reading the table saw
  four decisions where the spec records six. Both rows are now in the table, wording unchanged.
  They had been misplaced since the document was added, so `v26.1.0` and `v26.2.0` shipped with
  the table short; every other table in the file was checked and none is split.

## Performance

## Breaking Changes
