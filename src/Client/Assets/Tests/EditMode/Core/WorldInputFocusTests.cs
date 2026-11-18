using Mmorpg.Client.Gameplay;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class WorldInputFocusTests
    {
        // The interface holds the keyboard while any of its captures says so, and a capture that
        // is removed no longer counts.
        [Test]
        public void TheKeyboardIsHeldWhileACaptureHoldsIt()
        {
            var focus = new WorldInputFocus();
            var typing = false;
            var other = false;
            System.Func<bool> typingCapture = () => typing;
            System.Func<bool> otherCapture = () => other;

            Assert.That(focus.IsKeyboardHeldByInterface, Is.False);

            focus.AddKeyboardCapture(typingCapture);
            focus.AddKeyboardCapture(otherCapture);
            Assert.That(focus.IsKeyboardHeldByInterface, Is.False);

            typing = true;
            Assert.That(focus.IsKeyboardHeldByInterface, Is.True);

            typing = false;
            other = true;
            Assert.That(focus.IsKeyboardHeldByInterface, Is.True);

            focus.RemoveKeyboardCapture(otherCapture);
            Assert.That(focus.IsKeyboardHeldByInterface, Is.False);
        }

        // A capture of the pointer does not hold the keyboard, and the other way around.
        [Test]
        public void ThePointerAndTheKeyboardAreHeldApart()
        {
            var focus = new WorldInputFocus();
            focus.AddCapture(() => true);
            Assert.That(focus.IsKeyboardHeldByInterface, Is.False);

            var keyboardOnly = new WorldInputFocus();
            keyboardOnly.AddKeyboardCapture(() => true);
            Assert.That(keyboardOnly.IsPointerOverInterface(UnityEngine.Vector2.zero), Is.False);
        }
    }
}
