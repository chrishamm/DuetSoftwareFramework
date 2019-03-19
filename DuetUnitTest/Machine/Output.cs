﻿using DuetAPI;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Output
    {
        [Test]
        public void Clone()
        {
            DuetAPI.Machine.Model original = new DuetAPI.Machine.Model();

            original.Messages.Add(new Message
            {
                Content = "Test 1 2 3",
                Type = MessageType.Warning
            });

            original.Messages.Add(new Message
            {
                Content = "Err 3 2 1",
                Type = MessageType.Error
            });

            DuetAPI.Machine.Model clone = (DuetAPI.Machine.Model)original.Clone();

            Assert.AreEqual(2, original.Messages.Count);
            Assert.AreEqual(original.Messages[0].Content, "Test 1 2 3");
            Assert.AreEqual(original.Messages[0].Type, MessageType.Warning);
            Assert.AreEqual(original.Messages[1].Content, "Err 3 2 1");
            Assert.AreEqual(original.Messages[1].Type, MessageType.Error);
        }
    }
}
