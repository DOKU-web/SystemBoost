// GPU parity test: runs the real compute shaders (HLSL -> SPIR-V via glslang)
// on a Vulkan device (lavapipe in CI) with the same dispatch sequence as
// src/FrameGenGpu.cpp, so results can be compared with ref/fg_ref.py.
// Storage images are fp32 here (glslang declares R32f/Rgba32f); the D3D11 build
// uses R16F/RGBA8, so tiny quantization differences are expected.
//
// usage:
//   vk_parity fg <spv_dir> <W> <H> <procFactor> <refine> <radius> <A.bin> <B.bin> <out_prefix> t1 [t2 ...]
//   (optional env SBFG_PREV=<P.bin>: analyse P->A first and use its forward
//    field as the temporal candidate for A->B, like consecutive frames in the app)
//   vk_parity up <spv_dir> <W> <H> <outW> <outH> <sharpness> <in.bin> <out.bin>
// .bin = float32 RGBA, row-major, no header.
#include "../../src/FgConstants.h"
#include <vulkan/vulkan.h>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <map>
#include <string>
#include <vector>

using namespace sbfg;

#define VK(x) do { VkResult r_ = (x); if (r_ != VK_SUCCESS) { std::fprintf(stderr, "%s failed: %d (line %d)\n", #x, r_, __LINE__); std::exit(1); } } while (0)

static VkInstance inst;
static VkPhysicalDevice phys;
static VkDevice dev;
static VkQueue queue;
static uint32_t qfam;
static VkCommandPool cmdPool;
static VkDescriptorPool dpool;
static VkSampler sampler;

static uint32_t MemType(uint32_t bits, VkMemoryPropertyFlags want) {
    VkPhysicalDeviceMemoryProperties mp;
    vkGetPhysicalDeviceMemoryProperties(phys, &mp);
    for (uint32_t i = 0; i < mp.memoryTypeCount; ++i)
        if ((bits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & want) == want) return i;
    std::fprintf(stderr, "no memory type\n");
    std::exit(1);
}

struct Buf { VkBuffer b = VK_NULL_HANDLE; VkDeviceMemory m = VK_NULL_HANDLE; void* p = nullptr; VkDeviceSize size = 0; };
static Buf MakeBuf(VkDeviceSize size, VkBufferUsageFlags usage) {
    Buf b; b.size = size;
    VkBufferCreateInfo ci{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    ci.size = size; ci.usage = usage;
    VK(vkCreateBuffer(dev, &ci, nullptr, &b.b));
    VkMemoryRequirements mr; vkGetBufferMemoryRequirements(dev, b.b, &mr);
    VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    ai.allocationSize = mr.size;
    ai.memoryTypeIndex = MemType(mr.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    VK(vkAllocateMemory(dev, &ai, nullptr, &b.m));
    VK(vkBindBufferMemory(dev, b.b, b.m, 0));
    VK(vkMapMemory(dev, b.m, 0, size, 0, &b.p));
    return b;
}

static VkCommandBuffer Begin() {
    VkCommandBufferAllocateInfo ai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    ai.commandPool = cmdPool; ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; ai.commandBufferCount = 1;
    VkCommandBuffer cb; VK(vkAllocateCommandBuffers(dev, &ai, &cb));
    VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    VK(vkBeginCommandBuffer(cb, &bi));
    return cb;
}
static void Submit(VkCommandBuffer cb) {
    VK(vkEndCommandBuffer(cb));
    VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    si.commandBufferCount = 1; si.pCommandBuffers = &cb;
    VK(vkQueueSubmit(queue, 1, &si, VK_NULL_HANDLE));
    VK(vkQueueWaitIdle(queue));
    vkFreeCommandBuffers(dev, cmdPool, 1, &cb);
}

struct Img { VkImage img = VK_NULL_HANDLE; VkDeviceMemory mem; VkImageView view; uint32_t w = 0, h = 0, ch = 4; };
static Img MakeImg(uint32_t w, uint32_t h, uint32_t ch) {
    Img im; im.w = w; im.h = h; im.ch = ch;
    VkFormat fmt = ch == 1 ? VK_FORMAT_R32_SFLOAT : VK_FORMAT_R32G32B32A32_SFLOAT;
    VkImageCreateInfo ci{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ci.imageType = VK_IMAGE_TYPE_2D; ci.format = fmt; ci.extent = {w, h, 1};
    ci.mipLevels = 1; ci.arrayLayers = 1; ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    VK(vkCreateImage(dev, &ci, nullptr, &im.img));
    VkMemoryRequirements mr; vkGetImageMemoryRequirements(dev, im.img, &mr);
    VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    ai.allocationSize = mr.size; ai.memoryTypeIndex = MemType(mr.memoryTypeBits, 0);
    VK(vkAllocateMemory(dev, &ai, nullptr, &im.mem));
    VK(vkBindImageMemory(dev, im.img, im.mem, 0));
    VkImageViewCreateInfo vi{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
    vi.image = im.img; vi.viewType = VK_IMAGE_VIEW_TYPE_2D; vi.format = fmt;
    vi.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    VK(vkCreateImageView(dev, &vi, nullptr, &im.view));
    VkCommandBuffer cb = Begin();
    VkImageMemoryBarrier b{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
    b.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED; b.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    b.srcQueueFamilyIndex = b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = im.img; b.subresourceRange = vi.subresourceRange;
    b.dstAccessMask = VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT | VK_ACCESS_TRANSFER_WRITE_BIT;
    vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 1, &b);
    VkClearColorValue zero{}; VkImageSubresourceRange rr = vi.subresourceRange;
    vkCmdClearColorImage(cb, im.img, VK_IMAGE_LAYOUT_GENERAL, &zero, 1, &rr);
    Submit(cb);
    return im;
}

static void Upload(Img& im, const std::vector<float>& data) {
    Buf s = MakeBuf(data.size() * 4, VK_BUFFER_USAGE_TRANSFER_SRC_BIT);
    std::memcpy(s.p, data.data(), data.size() * 4);
    VkCommandBuffer cb = Begin();
    VkBufferImageCopy r{}; r.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1}; r.imageExtent = {im.w, im.h, 1};
    vkCmdCopyBufferToImage(cb, s.b, im.img, VK_IMAGE_LAYOUT_GENERAL, 1, &r);
    Submit(cb);
    vkDestroyBuffer(dev, s.b, nullptr); vkFreeMemory(dev, s.m, nullptr);
}

static std::vector<float> Download(const Img& im) {
    std::vector<float> out(size_t(im.w) * im.h * im.ch);
    Buf s = MakeBuf(out.size() * 4, VK_BUFFER_USAGE_TRANSFER_DST_BIT);
    VkCommandBuffer cb = Begin();
    VkBufferImageCopy r{}; r.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1}; r.imageExtent = {im.w, im.h, 1};
    vkCmdCopyImageToBuffer(cb, im.img, VK_IMAGE_LAYOUT_GENERAL, s.b, 1, &r);
    Submit(cb);
    std::memcpy(out.data(), s.p, out.size() * 4);
    vkDestroyBuffer(dev, s.b, nullptr); vkFreeMemory(dev, s.m, nullptr);
    return out;
}

// ------------------------------------------------------------------ pipelines
struct Pipe { VkDescriptorSetLayout dsl; VkPipelineLayout pl; VkPipeline p; bool ssbo7 = false, ssbo9 = false; };
static std::map<std::string, Pipe> pipes;
static std::string spvDir;
static Buf ubo;
static Img dummy;

static Pipe& GetPipe(const std::string& name) {
    auto it = pipes.find(name);
    if (it != pipes.end()) return it->second;
    Pipe P;
    P.ssbo7 = name == "interpolate";   // t5 = StructuredBuffer (scene stats)
    P.ssbo9 = name == "motion_median"; // u1 = RWStructuredBuffer
    std::vector<VkDescriptorSetLayoutBinding> b;
    auto add = [&](uint32_t i, VkDescriptorType t) { b.push_back({i, t, 1, VK_SHADER_STAGE_COMPUTE_BIT, nullptr}); };
    add(0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER);
    add(1, VK_DESCRIPTOR_TYPE_SAMPLER);
    for (uint32_t i = 2; i <= 6; ++i) add(i, VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE);
    add(7, P.ssbo7 ? VK_DESCRIPTOR_TYPE_STORAGE_BUFFER : VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE);
    add(8, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE);
    add(9, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER);
    VkDescriptorSetLayoutCreateInfo dci{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};
    dci.bindingCount = uint32_t(b.size()); dci.pBindings = b.data();
    VK(vkCreateDescriptorSetLayout(dev, &dci, nullptr, &P.dsl));
    VkPipelineLayoutCreateInfo pci{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};
    pci.setLayoutCount = 1; pci.pSetLayouts = &P.dsl;
    VK(vkCreatePipelineLayout(dev, &pci, nullptr, &P.pl));
    std::ifstream f(spvDir + "/" + name + ".spv", std::ios::binary);
    if (!f) { std::fprintf(stderr, "missing %s.spv\n", name.c_str()); std::exit(1); }
    std::vector<char> code((std::istreambuf_iterator<char>(f)), {});
    VkShaderModuleCreateInfo sci{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};
    sci.codeSize = code.size(); sci.pCode = reinterpret_cast<const uint32_t*>(code.data());
    VkShaderModule mod; VK(vkCreateShaderModule(dev, &sci, nullptr, &mod));
    VkComputePipelineCreateInfo cci{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO};
    cci.stage = {VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, nullptr, 0, VK_SHADER_STAGE_COMPUTE_BIT, mod, "main", nullptr};
    cci.layout = P.pl;
    VK(vkCreateComputePipelines(dev, VK_NULL_HANDLE, 1, &cci, nullptr, &P.p));
    return pipes[name] = P;
}

// srv: images for t0..t5 (nullptr = dummy); stats buffer goes to t5 or u1
static void Dispatch(const std::string& name, const FgConstants& c, std::vector<const Img*> srv,
                     const Img* uav, Buf* stats, uint32_t gx, uint32_t gy) {
    Pipe& P = GetPipe(name);
    std::memcpy(ubo.p, &c, sizeof c);
    VK(vkResetDescriptorPool(dev, dpool, 0));
    VkDescriptorSetAllocateInfo ai{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};
    ai.descriptorPool = dpool; ai.descriptorSetCount = 1; ai.pSetLayouts = &P.dsl;
    VkDescriptorSet ds; VK(vkAllocateDescriptorSets(dev, &ai, &ds));
    std::vector<VkWriteDescriptorSet> w;
    std::vector<VkDescriptorImageInfo> ii(16);
    std::vector<VkDescriptorBufferInfo> bi(4);
    VkWriteDescriptorSet base{VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET};
    base.dstSet = ds; base.descriptorCount = 1;
    bi[0] = {ubo.b, 0, sizeof(FgConstants)};
    { auto x = base; x.dstBinding = 0; x.descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER; x.pBufferInfo = &bi[0]; w.push_back(x); }
    ii[0] = {sampler, VK_NULL_HANDLE, VK_IMAGE_LAYOUT_GENERAL};
    { auto x = base; x.dstBinding = 1; x.descriptorType = VK_DESCRIPTOR_TYPE_SAMPLER; x.pImageInfo = &ii[0]; w.push_back(x); }
    srv.resize(6, nullptr);
    for (uint32_t i = 0; i < 6; ++i) {
        uint32_t binding = 2 + i;
        if (binding == 7 && P.ssbo7) continue;
        const Img* im = srv[i] ? srv[i] : &dummy;
        ii[1 + i] = {VK_NULL_HANDLE, im->view, VK_IMAGE_LAYOUT_GENERAL};
        auto x = base; x.dstBinding = binding; x.descriptorType = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE; x.pImageInfo = &ii[1 + i]; w.push_back(x);
    }
    ii[8] = {VK_NULL_HANDLE, (uav ? uav : &dummy)->view, VK_IMAGE_LAYOUT_GENERAL};
    { auto x = base; x.dstBinding = 8; x.descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE; x.pImageInfo = &ii[8]; w.push_back(x); }
    static Buf dummyBuf = MakeBuf(16, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT);
    Buf* sb = stats ? stats : &dummyBuf;
    bi[1] = {sb->b, 0, 16};
    if (P.ssbo7) { auto x = base; x.dstBinding = 7; x.descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER; x.pBufferInfo = &bi[1]; w.push_back(x); }
    { auto x = base; x.dstBinding = 9; x.descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER; x.pBufferInfo = &bi[1]; w.push_back(x); }
    vkUpdateDescriptorSets(dev, uint32_t(w.size()), w.data(), 0, nullptr);
    VkCommandBuffer cb = Begin();
    vkCmdBindPipeline(cb, VK_PIPELINE_BIND_POINT_COMPUTE, P.p);
    vkCmdBindDescriptorSets(cb, VK_PIPELINE_BIND_POINT_COMPUTE, P.pl, 0, 1, &ds, 0, nullptr);
    vkCmdDispatch(cb, gx, gy, 1);
    Submit(cb);
}

static uint32_t G(uint32_t n) { return (n + 7) / 8; }

static std::vector<float> ReadBin(const char* p, size_t n) {
    std::vector<float> v(n);
    std::ifstream f(p, std::ios::binary);
    f.read(reinterpret_cast<char*>(v.data()), std::streamsize(n * 4));
    if (!f) { std::fprintf(stderr, "cannot read %s\n", p); std::exit(1); }
    return v;
}
static void WriteBin(const std::string& p, const std::vector<float>& v) {
    std::ofstream f(p, std::ios::binary);
    f.write(reinterpret_cast<const char*>(v.data()), std::streamsize(v.size() * 4));
}

static void InitVulkan() {
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.apiVersion = VK_API_VERSION_1_1;
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ici.pApplicationInfo = &app;
    VK(vkCreateInstance(&ici, nullptr, &inst));
    uint32_t n = 0; vkEnumeratePhysicalDevices(inst, &n, nullptr);
    std::vector<VkPhysicalDevice> pds(n); vkEnumeratePhysicalDevices(inst, &n, pds.data());
    if (!n) { std::fprintf(stderr, "no vulkan device\n"); std::exit(1); }
    phys = pds[0];
    uint32_t qn = 0; vkGetPhysicalDeviceQueueFamilyProperties(phys, &qn, nullptr);
    std::vector<VkQueueFamilyProperties> qp(qn); vkGetPhysicalDeviceQueueFamilyProperties(phys, &qn, qp.data());
    for (qfam = 0; qfam < qn && !(qp[qfam].queueFlags & VK_QUEUE_COMPUTE_BIT); ++qfam) {}
    float prio = 1;
    VkDeviceQueueCreateInfo qci{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
    qci.queueFamilyIndex = qfam; qci.queueCount = 1; qci.pQueuePriorities = &prio;
    VkPhysicalDeviceFeatures feats{};
    feats.shaderStorageImageWriteWithoutFormat = VK_TRUE;
    feats.shaderStorageImageReadWithoutFormat = VK_TRUE;
    VkDeviceCreateInfo dci{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
    dci.queueCreateInfoCount = 1; dci.pQueueCreateInfos = &qci; dci.pEnabledFeatures = &feats;
    VK(vkCreateDevice(phys, &dci, nullptr, &dev));
    vkGetDeviceQueue(dev, qfam, 0, &queue);
    VkCommandPoolCreateInfo cpi{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    cpi.queueFamilyIndex = qfam; cpi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    VK(vkCreateCommandPool(dev, &cpi, nullptr, &cmdPool));
    VkDescriptorPoolSize ps[] = {{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 4}, {VK_DESCRIPTOR_TYPE_SAMPLER, 4},
                                 {VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, 16}, {VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 4},
                                 {VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 4}};
    VkDescriptorPoolCreateInfo dpi{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};
    dpi.maxSets = 2; dpi.poolSizeCount = 5; dpi.pPoolSizes = ps;
    VK(vkCreateDescriptorPool(dev, &dpi, nullptr, &dpool));
    VkSamplerCreateInfo sci{VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO};
    sci.magFilter = sci.minFilter = VK_FILTER_LINEAR;
    sci.mipmapMode = VK_SAMPLER_MIPMAP_MODE_LINEAR;
    sci.addressModeU = sci.addressModeV = sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.maxLod = 0;
    VK(vkCreateSampler(dev, &sci, nullptr, &sampler));
    ubo = MakeBuf(sizeof(FgConstants), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT);
    dummy = MakeImg(1, 1, 4);
}

// Same constants as FrameGenGpu::Base()
static FgConstants Base(uint32_t W, uint32_t H, uint32_t pw, uint32_t ph, int radius, int refine) {
    FgConstants c{};
    c.capSize[0] = W; c.capSize[1] = H; c.procSize[0] = pw; c.procSize[1] = ph;
    c.radius = radius; c.refineIters = uint32_t(refine);
    c.lam = 0.004f; c.zeroBias = 0.002f; c.fpIters = 2; c.fbSigma = 1.5f; c.fbSigmaK = 0.10f;
    c.staticThr = 0.02f; c.cutErr = 0.07f; c.cutRatio = 0.60f;
    c.outSize[0] = W; c.outSize[1] = H; c.sharpness = 0.5f;
    return c;
}

int main(int argc, char** argv) {
    if (argc < 3) { std::fprintf(stderr, "see header for usage\n"); return 1; }
    std::string mode = argv[1];
    spvDir = argv[2];
    InitVulkan();
    if (mode == "up") {
        uint32_t W = atoi(argv[3]), H = atoi(argv[4]), OW = atoi(argv[5]), OH = atoi(argv[6]);
        float sharp = float(atof(argv[7]));
        Img src = MakeImg(W, H, 4), tmp = MakeImg(OW, OH, 4), out = MakeImg(OW, OH, 4);
        Upload(src, ReadBin(argv[8], size_t(W) * H * 4));
        FgConstants c = Base(W, H, W / 2, H / 2, 6, 2);
        c.outSize[0] = OW; c.outSize[1] = OH; c.sharpness = sharp;
        Dispatch("upscale", c, {&src}, &tmp, nullptr, G(OW), G(OH));
        Dispatch("sharpen", c, {&tmp, nullptr}, &out, nullptr, G(OW), G(OH));
        WriteBin(argv[9], Download(out));
        return 0;
    }
    // ---- frame generation, mirrors FrameGenGpu::Resize/PushFrame/Generate
    uint32_t W = atoi(argv[3]), H = atoi(argv[4]);
    int pf = atoi(argv[5]), refine = atoi(argv[6]), radius = atoi(argv[7]);
    const char* aPath = argv[8]; const char* bPath = argv[9]; std::string prefix = argv[10];
    uint32_t pw = std::max<uint32_t>(16, W / pf), ph = std::max<uint32_t>(16, H / pf);
    std::vector<std::pair<uint32_t, uint32_t>> sizes{{pw, ph}};
    while (sizes.back().first / 2 >= 64) sizes.push_back({sizes.back().first / 2, sizes.back().second / 2});
    Img frame[2] = {MakeImg(W, H, 4), MakeImg(W, H, 4)};
    Upload(frame[0], ReadBin(aPath, size_t(W) * H * 4));
    Upload(frame[1], ReadBin(bPath, size_t(W) * H * 4));
    const char* prevPath = std::getenv("SBFG_PREV");
    Img frame2 = MakeImg(W, H, 4);
    if (prevPath) Upload(frame2, ReadBin(prevPath, size_t(W) * H * 4));
    std::vector<Img> pyr[3], fwdT, fwd, bwdT, bwd;
    for (auto& s : sizes) {
        for (int k = 0; k < 3; ++k) pyr[k].push_back(MakeImg(s.first, s.second, 1));
        for (auto* v : {&fwdT, &fwd, &bwdT, &bwd}) v->push_back(MakeImg((s.first + 7) / 8, (s.second + 7) / 8, 4));
    }
    Img smap = MakeImg(W, H, 1), gen = MakeImg(W, H, 4);
    Buf stats = MakeBuf(16, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT);
    std::memset(stats.p, 0, 16);

    FgConstants c = Base(W, H, pw, ph, radius, refine);
    Img prevFwd = MakeImg(fwd.empty() ? 1 : fwd[0].w, fwd.empty() ? 1 : fwd[0].h, 4);
    for (int k = 0; k < (prevPath ? 3 : 2); ++k) {  // BuildPyramid
        const Img& src = k < 2 ? frame[k] : frame2;
        Dispatch("luma_down", c, {&src}, &pyr[k][0], nullptr, G(pw), G(ph));
        for (size_t l = 1; l < sizes.size(); ++l) {
            FgConstants d = c; d.lvlSize[0] = pyr[k][l].w; d.lvlSize[1] = pyr[k][l].h;
            Dispatch("pyr_down", d, {&pyr[k][l - 1]}, &pyr[k][l], nullptr, G(pyr[k][l].w), G(pyr[k][l].h));
        }
    }
    auto motion = [&](int from, int to, std::vector<Img>& tmp, std::vector<Img>& out, bool st, const Img* temporal) {
        const int L = int(sizes.size());
        for (int l = L - 1; l >= 0; --l) {
            FgConstants d = c;
            d.lvlSize[0] = pyr[from][l].w; d.lvlSize[1] = pyr[from][l].h;
            d.gridSize[0] = out[l].w; d.gridSize[1] = out[l].h;
            d.isCoarse = l == L - 1; d.isFinest = l == 0;
            if (l < L - 1) { d.parentGrid[0] = out[l + 1].w; d.parentGrid[1] = out[l + 1].h; }
            d.hasTemporal = temporal != nullptr; d.temporalShift = uint32_t(l); d.accumStats = st && l == 0;
            const Img* parent = l < L - 1 ? &out[l + 1] : nullptr;
            Dispatch("motion_search", d, {&pyr[from][l], &pyr[to][l], parent, temporal}, &tmp[l], nullptr, G(out[l].w), G(out[l].h));
            Dispatch("motion_median", d, {&pyr[from][l], &pyr[to][l], &tmp[l]}, &out[l], &stats, G(out[l].w), G(out[l].h));
        }
    };
    const Img* temporal = nullptr;
    if (prevPath) {  // previous pair P->A (its stats are discarded)
        motion(2, 0, fwdT, fwd, true, nullptr);
        VkCommandBuffer cb = Begin();
        VkImageCopy r{}; r.srcSubresource = r.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
        r.extent = {fwd[0].w, fwd[0].h, 1};
        vkCmdCopyImage(cb, fwd[0].img, VK_IMAGE_LAYOUT_GENERAL, prevFwd.img, VK_IMAGE_LAYOUT_GENERAL, 1, &r);
        Submit(cb);
        std::memset(stats.p, 0, 16);
        temporal = &prevFwd;
    }
    motion(0, 1, fwdT, fwd, true, temporal);
    motion(1, 0, bwdT, bwd, false, nullptr);
    Dispatch("static_map", c, {&frame[0], &frame[1]}, &smap, nullptr, G(W), G(H));
    const uint32_t* sv = static_cast<const uint32_t*>(stats.p);
    double n = std::max(1u, sv[2]);
    std::printf("{\"meanBest\":%.5f,\"meanZero\":%.5f,\"blocks\":%u}\n", sv[0] / 65536.0 / n, sv[1] / 65536.0 / n, sv[2]);
    WriteBin(prefix + "_fwd.bin", Download(fwd[0]));
    for (int i = 11; i < argc; ++i) {
        FgConstants d = c; d.t = float(atof(argv[i]));
        Dispatch("interpolate", d, {&frame[0], &frame[1], &fwd[0], &bwd[0], &smap}, &gen, &stats, G(W), G(H));
        WriteBin(prefix + "_t" + std::to_string(i - 11) + ".bin", Download(gen));
    }
    return 0;
}
