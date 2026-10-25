# Terminal Binary Snapshot

- Apply the robustness principle: "be conservative in what you do, be liberal
  in what you accept from others."
  - Validate on encode
  - Gracefully degrade on decode, for example invalid styles degrade to
    ignoring the style.
  - Structural invariants are not degraded. Reserved mode bits above the
    registry are rejected rather than truncated.
