﻿using System;
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using InfinityTech.Runtime.Rendering.Core;

namespace InfinityTech.Runtime.Rendering.RDG
{
    public class RDGContext
    {
        public RenderWorld World;
        public RDGObjectPool ObjectPool;
        public CommandBuffer CmdBuffer;
        public ScriptableRenderContext RenderContext;
    }

    public struct RDGExecuteParams
    {
        public int currentFrameIndex;
    }

    public class RDGGraphBuilder 
    {
        internal struct CompiledResourceInfo
        {
            public List<int> producers;
            public List<int> consumers;
            public bool resourceCreated;
            public int refCount;

            public void Reset()
            {
                if (producers == null)
                    producers = new List<int>();
                if (consumers == null)
                    consumers = new List<int>();

                producers.Clear();
                consumers.Clear();
                resourceCreated = false;
                refCount = 0;
            }
        }

        internal struct CompiledPassInfo
        {
            public IRDGRenderPass pass;
            public List<int>[] resourceCreateList;
            public List<int>[] resourceReleaseList;
            public int refCount;
            public bool culled;
            public bool hasSideEffect;
            public int syncToPassIndex; // Index of the pass that needs to be waited for.
            public int syncFromPassIndex; // Smaller pass index that waits for this pass.
            public bool needGraphicsFence;
            public GraphicsFence fence;

            public bool enableAsyncCompute { get { return pass.enableAsyncCompute; } }
            public bool allowPassCulling { get { return pass.allowPassCulling; } }


            public void Reset(IRDGRenderPass pass)
            {
                this.pass = pass;

                if (resourceCreateList == null)
                {
                    resourceCreateList = new List<int>[2];
                    resourceReleaseList = new List<int>[2];
                    for (int i = 0; i < 2; ++i)
                    {
                        resourceCreateList[i] = new List<int>();
                        resourceReleaseList[i] = new List<int>();
                    }
                }

                for (int i = 0; i < 2; ++i)
                {
                    resourceCreateList[i].Clear();
                    resourceReleaseList[i].Clear();
                }

                refCount = 0;
                culled = false;
                hasSideEffect = false;
                syncToPassIndex = -1;
                syncFromPassIndex = -1;
                needGraphicsFence = false;
            }
        }

        string name;
        RDGResourceFactory m_Resources;
        RDGResourceScope<RDGBufferRef> m_BufferScope;
        RDGResourceScope<RDGTextureRef> m_TextureScope;
        RDGObjectPool m_RenderGraphPool = new RDGObjectPool();
        List<IRDGRenderPass> m_RenderPasses = new List<IRDGRenderPass>(64);
        Dictionary<int, ProfilingSampler> m_DefaultProfilingSamplers = new Dictionary<int, ProfilingSampler>();
        bool m_ExecutionExceptionWasRaised;
        RDGContext m_RenderGraphContext = new RDGContext();

        // Compiled Render Graph info.
        DynamicArray<CompiledResourceInfo>[] m_CompiledResourcesInfos = new DynamicArray<CompiledResourceInfo>[2];
        DynamicArray<CompiledPassInfo> m_CompiledPassInfos = new DynamicArray<CompiledPassInfo>();
        Stack<int> m_CullingStack = new Stack<int>();

        #region Public Interface

        public RDGGraphBuilder(string InName)
        {
            this.name = InName;
            m_Resources = new RDGResourceFactory();
            m_BufferScope = new RDGResourceScope<RDGBufferRef>();
            m_TextureScope = new RDGResourceScope<RDGTextureRef>();

            for (int i = 0; i < 2; ++i)
            {
                m_CompiledResourcesInfos[i] = new DynamicArray<CompiledResourceInfo>();
            }
        }

        public void Cleanup()
        {
            m_BufferScope.ClearScope();
            m_TextureScope.ClearScope();
            m_Resources.Cleanup();
        }

        public void PurgeUnusedResources()
        {
            m_Resources.CullingUnusedResources();
        }

        public RDGTextureRef ImportTexture(RenderTexture rt, int shaderProperty = 0)
        {
            return m_Resources.ImportTexture(rt, shaderProperty);
        }

        public RDGTextureRef CreateTexture(in RDGTextureDesc desc, int shaderProperty = 0)
        {
            return m_Resources.CreateTexture(desc, shaderProperty);
        }

        public RDGTextureRef CreateTexture(RDGTextureRef texture, int shaderProperty = 0)
        {
            return m_Resources.CreateTexture(m_Resources.GetTextureResourceDesc(texture.handle), shaderProperty);
        }

        public RDGTextureDesc GetTextureDesc(RDGTextureRef texture)
        {
            return m_Resources.GetTextureResourceDesc(texture.handle);
        }

        public RDGBufferRef ImportBuffer(ComputeBuffer buffer)
        {
            return m_Resources.ImportBuffer(buffer);
        }

        public RDGBufferRef CreateBuffer(in RDGBufferDesc desc)
        {
            return m_Resources.CreateBuffer(desc);
        }

        public RDGBufferRef CreateBuffer(in RDGBufferRef bufferHandle)
        {
            return m_Resources.CreateBuffer(m_Resources.GetBufferResourceDesc(bufferHandle.handle));
        }

        public RDGBufferDesc GetBufferDesc(in RDGBufferRef bufferHandle)
        {
            return m_Resources.GetBufferResourceDesc(bufferHandle.handle);
        }

        public RDGBufferRef ScopeBuffer(int Handle)
        {
            return m_BufferScope.Get(Handle);
        }

        public void ScopeBuffer(int Handle, RDGBufferRef Buffer)
        {
            m_BufferScope.Set(Handle, Buffer);
        }

        public RDGTextureRef ScopeTexture(int Handle)
        {
            return m_TextureScope.Get(Handle);
        }

        public void ScopeTexture(int Handle, RDGTextureRef Texture)
        {
            m_TextureScope.Set(Handle, Texture);
        }

        public void AddRenderPass<T>(string passName, ProfilingSampler sampler, StepAction<T> StepFunc, ExecuteAction<T> ExecuteFunc) where T : struct
        {
            var renderPass = m_RenderGraphPool.Get<RDGRenderPass<T>>();
            renderPass.Clear();
            renderPass.name = passName;
            renderPass.index = m_RenderPasses.Count;
            renderPass.PassData = m_RenderGraphPool.Get<T>();
            renderPass.customSampler = sampler;
            renderPass.StepFunc = StepFunc;
            renderPass.ExecuteFunc = ExecuteFunc;

            RDGPassBuilder PassBuilder = new RDGPassBuilder(renderPass, m_Resources);
            renderPass.Step(ref PassBuilder);

            m_RenderPasses.Add(renderPass);
        }

        public void Execute(ScriptableRenderContext renderContext, RenderWorld world, CommandBuffer cmd, int InFrameIndex)
        {
            m_ExecutionExceptionWasRaised = false;

            try
            {
                m_Resources.BeginRender(InFrameIndex);
                CompileRenderGraph();
                ExecuteRenderGraph(renderContext, world, cmd);
            } catch (Exception e) {
                Debug.LogError("Render Graph Execution error");
                if (!m_ExecutionExceptionWasRaised)
                    Debug.LogException(e);
                m_ExecutionExceptionWasRaised = true;
            } finally {
                ClearCompiledGraph();
                m_Resources.EndRender();
            }
        }
        #endregion

        #region Private Interface

        // Internal for testing purpose only
        internal DynamicArray<CompiledPassInfo> GetCompiledPassInfos() { return m_CompiledPassInfos; }

        private RDGGraphBuilder()
        {

        }

        // Internal for testing purpose only
        internal void ClearCompiledGraph()
        {
            ClearRenderPasses();
            m_Resources.Clear();

            for (int i = 0; i < 2; ++i)
                m_CompiledResourcesInfos[i].Clear();

            m_CompiledPassInfos.Clear();
        }

        void InitResourceInfosData(DynamicArray<CompiledResourceInfo> resourceInfos, int count)
        {
            resourceInfos.Resize(count);
            for (int i = 0; i < resourceInfos.size; ++i)
                resourceInfos[i].Reset();
        }

        void InitializeCompilationData()
        {
            InitResourceInfosData(m_CompiledResourcesInfos[(int)RDGResourceType.Texture], m_Resources.GetTextureResourceCount());
            InitResourceInfosData(m_CompiledResourcesInfos[(int)RDGResourceType.Buffer], m_Resources.GetBufferResourceCount());

            m_CompiledPassInfos.Resize(m_RenderPasses.Count);
            for (int i = 0; i < m_CompiledPassInfos.size; ++i)
                m_CompiledPassInfos[i].Reset(m_RenderPasses[i]);
        }

        void CountReferences()
        {
            for (int passIndex = 0; passIndex < m_CompiledPassInfos.size; ++passIndex)
            {
                ref CompiledPassInfo passInfo = ref m_CompiledPassInfos[passIndex];

                for (int type = 0; type < 2; ++type)
                {
                    var resourceRead = passInfo.pass.resourceReadLists[type];
                    foreach (var resource in resourceRead)
                    {
                        ref CompiledResourceInfo info = ref m_CompiledResourcesInfos[type][resource];
                        info.consumers.Add(passIndex);
                        info.refCount++;
                    }

                    var resourceWrite = passInfo.pass.resourceWriteLists[type];
                    foreach (var resource in resourceWrite)
                    {
                        ref CompiledResourceInfo info = ref m_CompiledResourcesInfos[type][resource];
                        info.producers.Add(passIndex);
                        passInfo.refCount++;

                        // Writing to an imported texture is considered as a side effect because we don't know what users will do with it outside of render graph.
                        if (m_Resources.IsResourceImported(resource))
                            passInfo.hasSideEffect = true;
                    }

                    foreach (int resourceIndex in passInfo.pass.temporalResourceList[type])
                    {
                        ref CompiledResourceInfo info = ref m_CompiledResourcesInfos[type][resourceIndex];
                        info.refCount++;
                        info.consumers.Add(passIndex);
                        info.producers.Add(passIndex);
                    }
                }
            }
        }

        void CulledOutputlessPasses()
        {
            m_CullingStack.Clear();
            for (int pass = 0; pass < m_CompiledPassInfos.size; ++pass)
            {
                ref CompiledPassInfo passInfo = ref m_CompiledPassInfos[pass];

                if (passInfo.refCount == 0 && !passInfo.hasSideEffect && passInfo.allowPassCulling)
                {
                    passInfo.culled = true;
                    for (int type = 0; type < 2; ++type)
                    {
                        foreach (var index in passInfo.pass.resourceReadLists[type])
                        {
                            m_CompiledResourcesInfos[type][index].refCount--;

                        }
                    }
                }
            }
        }

        void CulledUnusedPasses()
        {
            for (int type = 0; type < 2; ++type)
            {
                DynamicArray<CompiledResourceInfo> resourceUsageList = m_CompiledResourcesInfos[type];

                // Gather resources that are never read.
                m_CullingStack.Clear();
                for (int i = 0; i < resourceUsageList.size; ++i)
                {
                    if (resourceUsageList[i].refCount == 0)
                    {
                        m_CullingStack.Push(i);
                    }
                }

                while (m_CullingStack.Count != 0)
                {
                    var unusedResource = resourceUsageList[m_CullingStack.Pop()];
                    foreach (var producerIndex in unusedResource.producers)
                    {
                        ref var producerInfo = ref m_CompiledPassInfos[producerIndex];
                        producerInfo.refCount--;
                        if (producerInfo.refCount == 0 && !producerInfo.hasSideEffect && producerInfo.allowPassCulling)
                        {
                            producerInfo.culled = true;

                            foreach (var resourceIndex in producerInfo.pass.resourceReadLists[type])
                            {
                                ref CompiledResourceInfo resourceInfo = ref resourceUsageList[resourceIndex];
                                resourceInfo.refCount--;
                                // If a resource is not used anymore, add it to the stack to be processed in subsequent iteration.
                                if (resourceInfo.refCount == 0)
                                    m_CullingStack.Push(resourceIndex);
                            }
                        }
                    }
                }
            }
        }

        void UpdatePassSynchronization(ref CompiledPassInfo currentPassInfo, ref CompiledPassInfo producerPassInfo, int currentPassIndex, int lastProducer, ref int intLastSyncIndex)
        {
            // Current pass needs to wait for pass index lastProducer
            currentPassInfo.syncToPassIndex = lastProducer;
            // Update latest pass waiting for the other pipe.
            intLastSyncIndex = lastProducer;

            // Producer will need a graphics fence that this pass will wait on.
            producerPassInfo.needGraphicsFence = true;
            // We update the producer pass with the index of the smallest pass waiting for it.
            // This will be used to "lock" resource from being reused until the pipe has been synchronized.
            if (producerPassInfo.syncFromPassIndex == -1)
                producerPassInfo.syncFromPassIndex = currentPassIndex;
        }

        void UpdateResourceSynchronization(ref int lastGraphicsPipeSync, ref int lastComputePipeSync, int currentPassIndex, in CompiledResourceInfo resource)
        {
            int lastProducer = GetLatestProducerIndex(currentPassIndex, resource);
            if (lastProducer != -1)
            {
                ref CompiledPassInfo currentPassInfo = ref m_CompiledPassInfos[currentPassIndex];

                //If the passes are on different pipes, we need synchronization.
                if (m_CompiledPassInfos[lastProducer].enableAsyncCompute != currentPassInfo.enableAsyncCompute)
                {
                    // Pass is on compute pipe, need sync with graphics pipe.
                    if (currentPassInfo.enableAsyncCompute)
                    {
                        if (lastProducer > lastGraphicsPipeSync)
                        {
                            UpdatePassSynchronization(ref currentPassInfo, ref m_CompiledPassInfos[lastProducer], currentPassIndex, lastProducer, ref lastGraphicsPipeSync);
                        }
                    }
                    else
                    {
                        if (lastProducer > lastComputePipeSync)
                        {
                            UpdatePassSynchronization(ref currentPassInfo, ref m_CompiledPassInfos[lastProducer], currentPassIndex, lastProducer, ref lastComputePipeSync);
                        }
                    }
                }
            }
        }

        int GetLatestProducerIndex(int passIndex, in CompiledResourceInfo info)
        {
            // We want to know the highest pass index below the current pass that writes to the resource.
            int result = -1;
            foreach (var producer in info.producers)
            {
                // producers are by construction in increasing order.
                if (producer < passIndex)
                    result = producer;
                else
                    return result;
            }

            return result;
        }

        int GetLatestValidReadIndex(in CompiledResourceInfo info)
        {
            if (info.consumers.Count == 0)
                return -1;

            var consumers = info.consumers;
            for (int i = consumers.Count - 1; i >= 0; --i)
            {
                if (!m_CompiledPassInfos[consumers[i]].culled)
                    return consumers[i];
            }

            return -1;
        }

        int GetFirstValidWriteIndex(in CompiledResourceInfo info)
        {
            if (info.producers.Count == 0)
                return -1;

            var producers = info.producers;
            for (int i = 0; i < producers.Count; i++)
            {
                if (!m_CompiledPassInfos[producers[i]].culled)
                    return producers[i];
            }

            return -1;
        }

        int GetLatestValidWriteIndex(in CompiledResourceInfo info)
        {
            if (info.producers.Count == 0)
                return -1;

            var producers = info.producers;
            for (int i = producers.Count - 1; i >= 0; --i)
            {
                if (!m_CompiledPassInfos[producers[i]].culled)
                    return producers[i];
            }

            return -1;
        }


        void UpdateResourceAllocationAndSynchronization()
        {
            int lastGraphicsPipeSync = -1;
            int lastComputePipeSync = -1;

            // First go through all passes.
            // - Update the last pass read index for each resource.
            // - Add texture to creation list for passes that first write to a texture.
            // - Update synchronization points for all resources between compute and graphics pipes.
            for (int passIndex = 0; passIndex < m_CompiledPassInfos.size; ++passIndex)
            {
                ref CompiledPassInfo passInfo = ref m_CompiledPassInfos[passIndex];

                if (passInfo.culled)
                    continue;

                for (int type = 0; type < 2; ++type)
                {
                    var resourcesInfo = m_CompiledResourcesInfos[type];
                    foreach (int resource in passInfo.pass.resourceReadLists[type])
                    {
                        UpdateResourceSynchronization(ref lastGraphicsPipeSync, ref lastComputePipeSync, passIndex, resourcesInfo[resource]);
                    }

                    foreach (int resource in passInfo.pass.resourceWriteLists[type])
                    {
                        UpdateResourceSynchronization(ref lastGraphicsPipeSync, ref lastComputePipeSync, passIndex, resourcesInfo[resource]);
                    }

                }
            }

            for (int type = 0; type < 2; ++type)
            {
                var resourceInfos = m_CompiledResourcesInfos[type];
                // Now push resources to the release list of the pass that reads it last.
                for (int i = 0; i < resourceInfos.size; ++i)
                {
                    CompiledResourceInfo resourceInfo = resourceInfos[i];

                    // Resource creation
                    int firstWriteIndex = GetFirstValidWriteIndex(resourceInfo);
                    // Index -1 can happen for imported resources (for example an imported dummy black texture will never be written to but does not need creation anyway)
                    if (firstWriteIndex != -1)
                        m_CompiledPassInfos[firstWriteIndex].resourceCreateList[type].Add(i);

                    // Texture release
                    // Sometimes, a texture can be written by a pass after the last pass that reads it.
                    // In this case, we need to extend its lifetime to this pass otherwise the pass would get an invalid texture.
                    int lastReadPassIndex = Math.Max(GetLatestValidReadIndex(resourceInfo), GetLatestValidWriteIndex(resourceInfo));

                    if (lastReadPassIndex != -1)
                    {
                        // In case of async passes, we need to extend lifetime of resource to the first pass on the graphics pipeline that wait for async passes to be over.
                        // Otherwise, if we freed the resource right away during an async pass, another non async pass could reuse the resource even though the async pipe is not done.
                        if (m_CompiledPassInfos[lastReadPassIndex].enableAsyncCompute)
                        {
                            int currentPassIndex = lastReadPassIndex;
                            int firstWaitingPassIndex = m_CompiledPassInfos[currentPassIndex].syncFromPassIndex;
                            // Find the first async pass that is synchronized by the graphics pipeline (ie: passInfo.syncFromPassIndex != -1)
                            while (firstWaitingPassIndex == -1 && currentPassIndex < m_CompiledPassInfos.size)
                            {
                                currentPassIndex++;
                                if (m_CompiledPassInfos[currentPassIndex].enableAsyncCompute)
                                    firstWaitingPassIndex = m_CompiledPassInfos[currentPassIndex].syncFromPassIndex;
                            }

                            // Finally add the release command to the pass before the first pass that waits for the compute pipe.
                            ref CompiledPassInfo passInfo = ref m_CompiledPassInfos[Math.Max(0, firstWaitingPassIndex - 1)];
                            passInfo.resourceReleaseList[type].Add(i);

                            // Fail safe in case render graph is badly formed.
                            if (currentPassIndex == m_CompiledPassInfos.size)
                            {
                                IRDGRenderPass invalidPass = m_RenderPasses[lastReadPassIndex];
                                throw new InvalidOperationException($"Asynchronous pass {invalidPass.name} was never synchronized on the graphics pipeline.");
                            }
                        }
                        else
                        {
                            ref CompiledPassInfo passInfo = ref m_CompiledPassInfos[lastReadPassIndex];
                            passInfo.resourceReleaseList[type].Add(i);
                        }
                    }
                }
            }
        }

        internal void CompileRenderGraph()
        {
            InitializeCompilationData();
            CountReferences();
            CulledUnusedPasses();
            UpdateResourceAllocationAndSynchronization();
        }

        void ExecuteRenderGraph(ScriptableRenderContext RenderContext, RenderWorld renderWorld, CommandBuffer CmdBuffer)
        {
            using (new ProfilingScope(m_RenderGraphContext.CmdBuffer, ProfilingSampler.Get(ERGProfileId.InfinityRenderer)))
            {
                m_RenderGraphContext.World = renderWorld;
                m_RenderGraphContext.CmdBuffer = CmdBuffer;
                m_RenderGraphContext.RenderContext = RenderContext;
                m_RenderGraphContext.ObjectPool = m_RenderGraphPool;

                for (int passIndex = 0; passIndex < m_CompiledPassInfos.size; ++passIndex)
                {
                    ref var passInfo = ref m_CompiledPassInfos[passIndex];
                    if (passInfo.culled)
                        continue;

                    if (!passInfo.pass.HasRenderFunc())
                    {
                        throw new InvalidOperationException(string.Format("RenderPass {0} was not provided with an execute function.", passInfo.pass.name));
                    }

                    try
                    {
                        using (new ProfilingScope(m_RenderGraphContext.CmdBuffer, passInfo.pass.customSampler))
                        {
                            PreRenderPassExecute(passInfo, m_RenderGraphContext);
                            passInfo.pass.Execute(m_RenderGraphContext);
                            PostRenderPassExecute(CmdBuffer, ref passInfo, m_RenderGraphContext);
                        }
                    }
                    catch (Exception e)
                    {
                        m_ExecutionExceptionWasRaised = true;
                        Debug.LogError($"Render Graph Execution error at pass {passInfo.pass.name} ({passIndex})");
                        Debug.LogException(e);
                        throw;
                    }
                }
            }
        }

        void PreRenderPassSetRenderTargets(in CompiledPassInfo passInfo, RDGContext rgContext)
        {
            var pass = passInfo.pass;
            if (pass.depthBuffer.IsValid() || pass.colorBufferMaxIndex != -1)
            {
                var mrtArray = rgContext.ObjectPool.GetTempArray<RenderTargetIdentifier>(pass.colorBufferMaxIndex + 1);
                var colorBuffers = pass.colorBuffers;

                if (pass.colorBufferMaxIndex > 0)
                {
                    for (int i = 0; i <= pass.colorBufferMaxIndex; ++i)
                    {
                        if (!colorBuffers[i].IsValid())
                            throw new InvalidOperationException("MRT setup is invalid. Some indices are not used.");

                        mrtArray[i] = m_Resources.GetTexture(colorBuffers[i]);
                    }

                    if (pass.depthBuffer.IsValid())
                    {
                        using (new ProfilingScope(rgContext.CmdBuffer, ProfilingSampler.Get(ERGProfileId.GraphBuilderBind)))
                        {
                            CoreUtils.SetRenderTarget(rgContext.CmdBuffer, mrtArray, m_Resources.GetTexture(pass.depthBuffer));
                        }
                    } else {
                        throw new InvalidOperationException("Setting MRTs without a depth buffer is not supported.");
                    }
                } else {
                    if (pass.depthBuffer.IsValid())
                    {
                        if (pass.colorBufferMaxIndex > -1) {
                            using (new ProfilingScope(rgContext.CmdBuffer, ProfilingSampler.Get(ERGProfileId.GraphBuilderBind)))
                            {
                                CoreUtils.SetRenderTarget(rgContext.CmdBuffer, m_Resources.GetTexture(pass.colorBuffers[0]), m_Resources.GetTexture(pass.depthBuffer));
                            }
                        } else {
                            using (new ProfilingScope(rgContext.CmdBuffer, ProfilingSampler.Get(ERGProfileId.GraphBuilderBind)))
                            {
                                CoreUtils.SetRenderTarget(rgContext.CmdBuffer, m_Resources.GetTexture(pass.depthBuffer));
                            }
                        }
                    } else {
                        using (new ProfilingScope(rgContext.CmdBuffer, ProfilingSampler.Get(ERGProfileId.GraphBuilderBind)))
                        {
                            CoreUtils.SetRenderTarget(rgContext.CmdBuffer, m_Resources.GetTexture(pass.colorBuffers[0]));
                        }
                    }

                }
            }
        }

        void PreRenderPassExecute(in CompiledPassInfo passInfo, RDGContext rgContext)
        {
            // TODO RENDERGRAPH merge clear and setup here if possible
            IRDGRenderPass pass = passInfo.pass;

            // TODO RENDERGRAPH remove this when we do away with auto global texture setup
            // (can't put it in the profiling scope otherwise it might be executed on compute queue which is not possible for global sets)
            m_Resources.SetGlobalTextures(rgContext, pass.resourceReadLists[(int)RDGResourceType.Texture]);

            foreach (var BufferHandle in passInfo.resourceCreateList[(int)RDGResourceType.Buffer])
                m_Resources.CreateRealBuffer(BufferHandle);

            foreach (var texture in passInfo.resourceCreateList[(int)RDGResourceType.Texture])
                m_Resources.CreateRealTexture(rgContext, texture);

            PreRenderPassSetRenderTargets(passInfo, rgContext);

            // Flush first the current command buffer on the render context.
            rgContext.RenderContext.ExecuteCommandBuffer(rgContext.CmdBuffer);
            rgContext.CmdBuffer.Clear();

            if (pass.enableAsyncCompute)
            {
                CommandBuffer AsyncCmdBuffer = CommandBufferPool.Get(pass.name);
                AsyncCmdBuffer.SetExecutionFlags(CommandBufferExecutionFlags.AsyncCompute);
                rgContext.CmdBuffer = AsyncCmdBuffer;
            }

            // Synchronize with graphics or compute pipe if needed.
            if (passInfo.syncToPassIndex != -1)
            {
                rgContext.CmdBuffer.WaitOnAsyncGraphicsFence(m_CompiledPassInfos[passInfo.syncToPassIndex].fence);
            }
        }

        void PostRenderPassExecute(CommandBuffer mainCmd, ref CompiledPassInfo passInfo, RDGContext rgContext)
        {
            IRDGRenderPass pass = passInfo.pass;

            if (passInfo.needGraphicsFence)
                passInfo.fence = rgContext.CmdBuffer.CreateAsyncGraphicsFence();

            if (pass.enableAsyncCompute)
            {
                // The command buffer has been filled. We can kick the async task.
                rgContext.RenderContext.ExecuteCommandBufferAsync(rgContext.CmdBuffer, ComputeQueueType.Background);
                CommandBufferPool.Release(rgContext.CmdBuffer);
                rgContext.CmdBuffer = mainCmd; // Restore the main command buffer.
            }

            m_RenderGraphPool.ReleaseAllTempAlloc();

            foreach (var buffer in passInfo.resourceReleaseList[(int)RDGResourceType.Buffer])
                m_Resources.ReleaseRealBuffer(buffer);

            foreach (var texture in passInfo.resourceReleaseList[(int)RDGResourceType.Texture])
                m_Resources.ReleaseRealTexture(texture);

        }

        void ClearRenderPasses()
        {
            foreach (var pass in m_RenderPasses)
            {
                pass.Release(m_RenderGraphPool);
            }

            m_RenderPasses.Clear();

            m_BufferScope.ClearScope();
            m_TextureScope.ClearScope();
        }

        ProfilingSampler GetDefaultProfilingSampler(string name)
        {
            int hash = name.GetHashCode();
            if (!m_DefaultProfilingSamplers.TryGetValue(hash, out var sampler))
            {
                sampler = new ProfilingSampler(name);
                m_DefaultProfilingSamplers.Add(hash, sampler);
            }

            return sampler;
        }

        #endregion
    }
}
