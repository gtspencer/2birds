using System.Collections.Generic;

namespace TwoBirds
{
    internal sealed class RuntimeItemIds
    {
        private uint next;
        internal void Seed(IEnumerable<uint> baked)
        {
            next = 0;
            foreach (uint id in baked) if (id > next) next = id;
        }
        internal uint Allocate() => ++next;
    }
}
