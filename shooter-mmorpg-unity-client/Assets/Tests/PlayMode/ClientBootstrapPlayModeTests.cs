using System.Collections;
using NUnit.Framework;
using ShooterMmo.Ui;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ShooterMmo.Tests.PlayMode
{
    public sealed class ClientBootstrapPlayModeTests
    {
        [UnityTest]
        public IEnumerator LoadingLoginSceneCreatesTheRuntimeLoginPanel()
        {
            var loadOperation = SceneManager.LoadSceneAsync(ShooterMmoSceneNames.LoginMenu);
            Assert.That(loadOperation, Is.Not.Null);

            while (!loadOperation.isDone)
            {
                yield return null;
            }

            yield return null;

            Assert.That(Object.FindFirstObjectByType<ShooterMmoClientBootstrap>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<LoginMenuPanel>(), Is.Not.Null);
        }
    }
}
