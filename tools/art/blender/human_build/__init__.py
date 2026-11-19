"""Tools that build the nets of the human base bodies from their reference meshes: a coarse cage
of quads designed once and shared by both bodies, subdivided once, laid onto each body's reference,
and relaxed along its surface, as the Modeling Rules in docs/ART_PIPELINE.md describe. They make a
starting point: once a body is approved, its .blend file is the source and a person edits it."""
