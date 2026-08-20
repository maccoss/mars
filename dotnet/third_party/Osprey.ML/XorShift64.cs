/*
 * Original author: Brendan MacLean <brendanx .at. uw.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Based on osprey (https://github.com/MacCossLab/osprey)
 *   by Michael J. MacCoss, MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2026 University of Washington - Seattle, WA
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

// VENDORED FRAGMENT. Upstream this class lives inside
// pwiz_tools/Osprey/Osprey.ML/LinearSvmClassifier.cs, a file that also pulls in
// MathNet.Numerics and Osprey.Core and so cannot be vendored whole. Only the PRNG is
// reproduced here, because GradientBoostedTrees.cs depends on it for subsampling.
//
// The drift guard for this file is SEMANTIC, not textual: a PRNG's contract is the
// sequence it emits, so MARS.Test asserts the first outputs for a fixed seed against
// values taken from the upstream implementation. A reformatting upstream is harmless; a
// change to the shift constants would fail loudly, which is the case that matters.

namespace pwiz.Osprey.ML
{
    /// <summary>
    /// Deterministic xorshift64 PRNG for reproducible shuffling.
    /// Matches the Rust implementation: x ^= x &lt;&lt; 13; x ^= x &gt;&gt; 7; x ^= x &lt;&lt; 17.
    /// </summary>
    public class XorShift64
    {
        private ulong _state;

        public XorShift64(ulong seed)
        {
            // Ensure non-zero state
            _state = seed == 0 ? 1UL : seed;
        }

        public ulong Next()
        {
            ulong x = _state;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            _state = x;
            return x;
        }
    }
}
