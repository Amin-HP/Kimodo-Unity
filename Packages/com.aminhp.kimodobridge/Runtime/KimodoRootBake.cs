// SPDX-License-Identifier: Apache-2.0
// How a baked/previewed clip's root translation is authored. Lives in the runtime
// assembly so both the runtime components (KimodoGenerator) and the editor baker can
// reference it.

namespace AminHP.KimodoBridge
{
    /// <summary>How a baked clip's root translation is authored.</summary>
    public enum KimodoRootBake
    {
        /// <summary>Horizontal root travel is stripped, so the character walks on the spot cleanly
        /// (vertical bob kept). Looks the same with or without Apply Root Motion.</summary>
        InPlace,

        /// <summary>Root motion is kept. On an Animator with "Apply Root Motion" ON the character
        /// travels; with it OFF it walks on the spot.</summary>
        Travel,
    }
}
