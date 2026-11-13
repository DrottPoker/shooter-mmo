using System.Collections;
using System.Collections.Generic;

namespace Mmorpg.Client.Tests.EditMode
{
    // Runs a coroutine by hand as Unity does: a nested routine runs at once, and each step goes
    // to the next wait.
    internal sealed class TestRoutine
    {
        private readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();

        public TestRoutine(IEnumerator routine)
        {
            stack.Push(routine);
        }

        public bool IsDone
        {
            get { return stack.Count == 0; }
        }

        // Runs to the next wait; false once the routine ended.
        public bool Step()
        {
            while (stack.Count > 0)
            {
                var current = stack.Peek();
                if (!current.MoveNext())
                {
                    stack.Pop();
                    continue;
                }

                if (current.Current is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }

                return true;
            }

            return false;
        }
    }
}
