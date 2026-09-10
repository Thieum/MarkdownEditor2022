namespace MarkdownEditor2022.UnitTests
{
    [TestClass]
    public class PreviewScrollSyncTests
    {
        [TestMethod]
        public void EditorRequest_CanApply()
        {
            PreviewScrollSync sync = new();

            Assert.IsTrue(sync.CanApply(sync.RequestSync(fromEditor: true)));
        }

        [TestMethod]
        public void NewerRequest_InvalidatesQueuedRequest()
        {
            PreviewScrollSync sync = new();
            int oldRequest = sync.RequestSync(fromEditor: true);
            int newRequest = sync.RequestSync(fromEditor: true);

            Assert.IsFalse(sync.CanApply(oldRequest));
            Assert.IsTrue(sync.CanApply(newRequest));
        }

        [TestMethod]
        public void PreviewInput_CancelsQueuedEditorScroll()
        {
            PreviewScrollSync sync = new();
            int request = sync.RequestSync(fromEditor: true);

            sync.OnPreviewInteraction("wheel-1");

            Assert.IsFalse(sync.CanApply(request));
            Assert.IsTrue(sync.PreviewOwnsScroll);
        }

        [TestMethod]
        public void Refresh_DoesNotTakeScrollBackFromPreview()
        {
            PreviewScrollSync sync = new();
            sync.OnPreviewInteraction("wheel-1");

            int refresh = sync.RequestSync(fromEditor: false);

            Assert.IsFalse(sync.CanApply(refresh));
            Assert.IsFalse(sync.CanApply(sync.Version));
            Assert.IsTrue(sync.PreviewOwnsScroll);
        }

        [TestMethod]
        public void EditorScroll_ResumesSyncWithoutRevivingOldRequest()
        {
            PreviewScrollSync sync = new();
            int oldRequest = sync.RequestSync(fromEditor: true);
            sync.OnPreviewInteraction("wheel-1");

            int newRequest = sync.RequestSync(fromEditor: true);

            Assert.IsTrue(sync.CanApply(newRequest));
            Assert.IsFalse(sync.CanApply(oldRequest));
            Assert.AreEqual("wheel-1", sync.InputToken);
        }

        [TestMethod]
        public void FurtherPreviewInput_CancelsResumedSync()
        {
            PreviewScrollSync sync = new();
            sync.OnPreviewInteraction("wheel-1");
            int request = sync.RequestSync(fromEditor: true);

            sync.OnPreviewInteraction("wheel-2");

            Assert.IsFalse(sync.CanApply(request));
            Assert.AreEqual("wheel-2", sync.InputToken);
        }

        [TestMethod]
        public void InitialRender_CanSyncBeforePreviewInput()
        {
            PreviewScrollSync sync = new();

            Assert.IsTrue(sync.CanApply(sync.Version));
        }
    }
}
