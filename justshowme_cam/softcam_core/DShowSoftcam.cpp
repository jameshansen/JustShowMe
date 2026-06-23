#include "DShowSoftcam.h"

#include <cstring>
#include <string>
#include <algorithm>
#include <cstdio>
#include <cmath>
#include <chrono>
#include <ctime>


namespace {

//#define ENABLE_LOG
//#define LOG_FILE_PATH "C:\\my_temp\\debug_log.txt"

#if defined(ENABLE_LOG)

FILE* logfile = nullptr;
#define OPEN_LOGFILE() logfile = std::fopen(LOG_FILE_PATH, "a")
#define CLOSE_LOGFILE() [&]{ \
        if (logfile) std::fclose(logfile); \
        logfile = nullptr; \
    }()
#define NOW() []{ \
        auto tp = std::chrono::system_clock::now(); \
        auto t = std::chrono::system_clock::to_time_t(tp); \
        static char buff[128]; \
        std::strftime(buff, sizeof(buff), "%H:%M:%S", std::localtime(&t)); \
        return buff; \
    }()
std::string IID_TO_STR(REFIID riid)
{
    std::string s =
            riid == IID_IPin                ? "IPin" :
            riid == IID_IBaseFilter         ? "IBaseFilter" :
            riid == IID_IAMovieSetup        ? "IAMovieSetup" :
            riid == IID_IQualityControl     ? "IQualityControl" :
            riid == IID_IAMStreamConfig     ? "IAMStreamConfig" :
            riid == IID_IKsPropertySet      ? "IKsPropertySet" :
            riid == IID_IAMFilterMiscFlags  ? "IAMFilterMiscFlags" :
            riid == IID_IPersistPropertyBag ? "IPersistPropertyBag" :
            riid == IID_IReferenceClock     ? "IReferenceClock" :
            riid == IID_IMediaSeeking       ? "IMediaSeeking" :
            riid == IID_IAMDeviceRemoval    ? "IAMDeviceRemoval" :
            riid == IID_IAMOpenProgress     ? "IAMOpenProgress" :
            riid == IID_IMediaPosition      ? "IMediaPosition" :
            riid == IID_IMediaFilter        ? "IMediaFilter" :
            riid == IID_IBasicVideo         ? "IBasicVideo" :
            riid == IID_IBasicAudio         ? "IBasicAudio" :
            riid == IID_IVideoWindow        ? "IVideoWindow" :
            riid == IID_IUnknown            ? "IUnknown" :
            [&]() -> std::string
            {
                char buff[128];
                snprintf(buff, sizeof(buff),
                    "{%08lX-%04hX-%04hX-%02hhX%02hhX-%02hhX%02hhX%02hhX%02hhX%02hhX%02hhX}",
                    riid.Data1, riid.Data2, riid.Data3,
                    riid.Data4[0], riid.Data4[1], riid.Data4[2], riid.Data4[3],
                    riid.Data4[4], riid.Data4[5], riid.Data4[6], riid.Data4[7]);
                return buff;
            }();
    return s;
}
#define LOG(fmt, ...) [&,funcname=__func__]{ \
        if (logfile) std::fprintf(logfile, "%s: %s " fmt, NOW(), funcname, __VA_ARGS__); \
        std::printf("%s: %s " fmt, NOW(), funcname, __VA_ARGS__); \
    }()

#else // ENABLE_LOG

#define OPEN_LOGFILE()
#define CLOSE_LOGFILE()
#define LOG(...)

#endif // ENABLE_LOG


AM_MEDIA_TYPE* allocateMediaType()
{
    BYTE *pbFormat = (BYTE*)CoTaskMemAlloc(sizeof(VIDEOINFOHEADER));
    if (!pbFormat)
    {
        return nullptr;
    }

    AM_MEDIA_TYPE *amt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!amt)
    {
        CoTaskMemFree(pbFormat);
        return nullptr;
    }
    std::memset(amt, 0, sizeof(*amt));
    amt->pbFormat = pbFormat;
    return amt;
}

std::size_t calcDIBSize(int width, int height)
{
    std::size_t stride = (static_cast<unsigned>(width) * 3 + 3) & ~3u;
    return stride * static_cast<unsigned>(height);
}

void fillMediaType(AM_MEDIA_TYPE* amt, int width, int height, float framerate)
{
    BYTE *pbFormat = amt->pbFormat;

    if (framerate <= 0.0f)
    {
        framerate = 60.0f;
    }
    const float bit_rate = (float)width * (float)height * 24 * framerate;
    const float period = 10 * 1000 * 1000 / framerate;

    VIDEOINFOHEADER* pFormat = (VIDEOINFOHEADER*)pbFormat;
    std::memset(pFormat, 0, sizeof(*pFormat));
    pFormat->dwBitRate = (uint32_t)(std::min)(bit_rate, (float)INT_MAX);
    pFormat->dwBitErrorRate = 0;
    pFormat->AvgTimePerFrame = (LONGLONG)std::round((std::min)(period, (float)LONG_MAX));
    pFormat->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pFormat->bmiHeader.biWidth = width;
    pFormat->bmiHeader.biHeight = height;
    pFormat->bmiHeader.biPlanes = 1;
    pFormat->bmiHeader.biBitCount = 24;
    pFormat->bmiHeader.biCompression = BI_RGB;
    pFormat->bmiHeader.biSizeImage = static_cast<uint32_t>(calcDIBSize(width, height));

    amt->majortype = MEDIATYPE_Video;
    amt->subtype = MEDIASUBTYPE_RGB24;
    amt->bFixedSizeSamples = TRUE;
    amt->bTemporalCompression = FALSE;
    amt->lSampleSize = static_cast<uint32_t>(calcDIBSize(width, height));
    amt->formattype = FORMAT_VideoInfo;
    amt->pUnk = nullptr;
    amt->cbFormat = sizeof(VIDEOINFOHEADER);
    amt->pbFormat = pbFormat;
}

AM_MEDIA_TYPE* makeMediaType(int width, int height, float framerate)
{
    AM_MEDIA_TYPE *amt = allocateMediaType();
    if (!amt)
    {
        return nullptr;
    }
    fillMediaType(amt, width, height, framerate);
    return amt;
}

} //namespace

namespace softcam {


CUnknown * Softcam::CreateInstance(
                    LPUNKNOWN   lpunk,
                    const GUID& clsid,
                    HRESULT*    phr)
{
    OPEN_LOGFILE();
    LOG("===== logging started =====\n");

    return new Softcam(lpunk, clsid, phr);
}

Softcam::Softcam(LPUNKNOWN lpunk, const GUID& clsid, HRESULT *phr) :
    CSource(NAME("JustShowMe Virtual Webcam"), lpunk, clsid),
    m_frame_buffer(FrameBuffer::open()),
    // Always valid, even with no sender, so the camera still appears and can show a
    // placeholder. Default to 640x480/30 when no sender is running (matches the GUI's
    // default virtual-cam resolution so the buffer is adopted once the GUI starts).
    m_valid(true),
    m_width(m_frame_buffer ? m_frame_buffer.width() : 640),
    m_height(m_frame_buffer ? m_frame_buffer.height() : 480),
    m_framerate(m_frame_buffer ? m_frame_buffer.framerate() : 30.0f)
{
    // This code is okay though it may look strange as the return value is ignored.
    // Calling the SoftcamStream constructor results in calling the CBaseOutputPin
    // constructor which registers the instance to this Softcam instance by calling
    // CSource::AddPin().
    (void)new SoftcamStream(phr, this, L"JustShowMe Virtual Webcam Stream");
}


STDMETHODIMP Softcam::NonDelegatingQueryInterface(REFIID riid, __deref_out void **ppv)
{
    if (riid == IID_IAMStreamConfig)
    {
        LOG("(Softcam) IAMStreamConfig -> S_OK\n");
        return GetInterface(static_cast<IAMStreamConfig*>(this), ppv);
    }
    else
    {
        auto result = CSource::NonDelegatingQueryInterface(riid, ppv);
        LOG("(Softcam) %s -> %s\n", IID_TO_STR(riid).c_str(), result ? "(ERROR)" : "S_OK");
        return result;
    }
}

HRESULT
Softcam::SetFormat(AM_MEDIA_TYPE *mt)
{
    if (!mt)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    if (mt->majortype != MEDIATYPE_Video ||
        mt->subtype != MEDIASUBTYPE_RGB24)
    {
        LOG("-> E_FAIL (invalid media type)\n");
        return E_FAIL;
    }
    if (mt->formattype == FORMAT_VideoInfo && mt->pbFormat)
    {
        VIDEOINFOHEADER* pFormat = (VIDEOINFOHEADER*)mt->pbFormat;
        if (pFormat->bmiHeader.biWidth != m_width ||
            pFormat->bmiHeader.biHeight != m_height)
        {
            LOG("-> E_FAIL (invalid dimension)\n");
            return E_FAIL;
        }
        if (pFormat->bmiHeader.biBitCount != 24 ||
            pFormat->bmiHeader.biCompression != BI_RGB)
        {
            LOG("-> E_FAIL (invalid color format)\n");
            return E_FAIL;
        }
    }
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT
Softcam::GetFormat(AM_MEDIA_TYPE **out_pmt)
{
    if (!out_pmt)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    AM_MEDIA_TYPE* mt = makeMediaType(m_width, m_height, m_framerate);
    if (!mt)
    {
        LOG("-> E_OUTOFMEMORY\n");
        return E_OUTOFMEMORY;
    }
    *out_pmt = mt;
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT
Softcam::GetNumberOfCapabilities(int *out_count, int *out_size)
{
    if (!out_count || !out_size)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    *out_count = 1;
    *out_size = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT
Softcam::GetStreamCaps(int index, AM_MEDIA_TYPE **out_pmt, BYTE *out_scc)
{
    if (!out_pmt || !out_scc)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    if (index >= 1)
    {
        LOG("-> S_FALSE (invalid index)\n");
        return S_FALSE;
    }
    AM_MEDIA_TYPE *mt = makeMediaType(m_width, m_height, m_framerate);
    if (!mt)
    {
        LOG("-> E_OUTOFMEMORY\n");
        return E_OUTOFMEMORY;
    }
    *out_pmt = mt;
    std::memset(out_scc, 0, sizeof(VIDEO_STREAM_CONFIG_CAPS));
    VIDEOINFOHEADER* format = (VIDEOINFOHEADER*)mt->pbFormat;
    VIDEO_STREAM_CONFIG_CAPS* scc = (VIDEO_STREAM_CONFIG_CAPS*)out_scc;
    scc->guid = FORMAT_VideoInfo;
    scc->InputSize = SIZE{m_width, m_height};
    scc->MinCroppingSize = SIZE{m_width, m_height};
    scc->MaxCroppingSize = SIZE{m_width, m_height};
    scc->CropGranularityX = 1;
    scc->CropGranularityY = 1;
    scc->CropAlignX = 1;
    scc->CropAlignY = 1;
    scc->MinOutputSize = SIZE{m_width, m_height};
    scc->MaxOutputSize = SIZE{m_width, m_height};
    scc->OutputGranularityX = 1;
    scc->OutputGranularityY = 1;
    scc->StretchTapsX = 0;
    scc->StretchTapsY = 0;
    scc->ShrinkTapsX = 0;
    scc->ShrinkTapsY = 0;
    scc->MinFrameInterval = format->AvgTimePerFrame;
    scc->MaxFrameInterval = format->AvgTimePerFrame;
    scc->MinBitsPerSecond = (LONG)format->dwBitRate;
    scc->MaxBitsPerSecond = (LONG)format->dwBitRate;
    LOG("-> S_OK\n");
    return S_OK;
}


FrameBuffer* Softcam::getFrameBuffer()
{
    if (!m_valid)
    {
        return nullptr;
    }

    CAutoLock lock(&m_critsec);
    if (!m_frame_buffer)
    {
        auto fb = FrameBuffer::open();
        if (fb &&
            fb.active() &&
            fb.width() == m_width &&
            fb.height() == m_height)
        {
            m_frame_buffer = fb;
        }
    }
    if (m_frame_buffer)
    {
        return &m_frame_buffer;
    }
    else
    {
        return nullptr;
    }
}

void
Softcam::releaseFrameBuffer()
{
    CAutoLock lock(&m_critsec);
    m_frame_buffer.release();
}

SoftcamStream::SoftcamStream(HRESULT *phr,
                         Softcam *pParent,
                         LPCWSTR pPinName) :
    CSourceStream(NAME("JustShowMe Virtual Webcam Stream"), phr, pParent, pPinName),
    m_valid(pParent->valid()),
    m_width(pParent->width()),
    m_height(pParent->height())
{
}


SoftcamStream::~SoftcamStream()
{
    LOG("===== logging finished =====\n");
    CLOSE_LOGFILE();
}


STDMETHODIMP SoftcamStream::NonDelegatingQueryInterface(REFIID riid, __deref_out void **ppv)
{
    if (riid == IID_IKsPropertySet )
    {
        LOG("(SoftcamStream) IKsPropertySet -> S_OK\n");
        return GetInterface(static_cast<IKsPropertySet*>(this), ppv);
    }
    else if(riid == IID_IAMStreamConfig)
    {
        LOG("(SoftcamStream) IAMStreamConfig -> S_OK\n");
        return GetInterface(static_cast<IAMStreamConfig*>(this), ppv);
    }
    else
    {
        auto result = CSourceStream::NonDelegatingQueryInterface(riid, ppv);
        LOG("(SoftcamStream) %s -> %s\n", IID_TO_STR(riid).c_str(), result ? "(ERROR)" : "S_OK");
        return result;
    }
}


void SoftcamStream::fillPlaceholder(BYTE* pData)
{
    const int w = m_width, h = m_height;
    const std::size_t size = calcDIBSize(w, h);
    const int stride = (int)((static_cast<unsigned>(w) * 3 + 3) & ~3u);

    // Render once into a cached bottom-up RGB24 buffer, then just copy each frame.
    if (!m_placeholder)
    {
        m_placeholder.reset(new uint8_t[size]);

        BITMAPINFO bmi;
        std::memset(&bmi, 0, sizeof(bmi));
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h;          // top-down for easy drawing
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 24;
        bmi.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HDC hdc = CreateCompatibleDC(nullptr);
        HBITMAP hbm = CreateDIBSection(hdc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
        HGDIOBJ oldbm = SelectObject(hdc, hbm);

        RECT rc = { 0, 0, w, h };
        HBRUSH bg = CreateSolidBrush(RGB(0, 32, 160));   // blue
        FillRect(hdc, &rc, bg);
        DeleteObject(bg);

        SetBkMode(hdc, TRANSPARENT);
        SetTextColor(hdc, RGB(255, 230, 0));             // yellow
        HFONT font = CreateFontA(h / 12, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
            ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_DONTCARE, "Segoe UI");
        HGDIOBJ oldfont = SelectObject(hdc, font);

        const char* text = "Please launch\nJustShowMe GUI";

        // Measure the text block.
        RECT calc = { 0, 0, w, h };
        DrawTextA(hdc, text, -1, &calc, DT_CENTER | DT_WORDBREAK | DT_CALCRECT);
        int th = calc.bottom - calc.top;

        // Top half: normal — reads correctly in the real (un-mirrored) feed and recordings.
        int topY = (h / 2 - th) / 2; if (topY < 0) topY = 0;
        RECT top = { 0, topY, w, topY + th };
        DrawTextA(hdc, text, -1, &top, DT_CENTER | DT_WORDBREAK);

        // Bottom half: horizontally mirrored — reads correctly in Zoom's mirrored
        // self-preview. Flip the world transform about the vertical centre line.
        int botY = h / 2 + (h / 2 - th) / 2; if (botY < h / 2) botY = h / 2;
        RECT bot = { 0, botY, w, botY + th };
        SetGraphicsMode(hdc, GM_ADVANCED);
        XFORM xf = { -1.0f, 0.0f, 0.0f, 1.0f, (float)w, 0.0f }; // x -> w - x
        SetWorldTransform(hdc, &xf);
        DrawTextA(hdc, text, -1, &bot, DT_CENTER | DT_WORDBREAK);
        ModifyWorldTransform(hdc, NULL, MWT_IDENTITY);
        SetGraphicsMode(hdc, GM_COMPATIBLE);

        GdiFlush();

        // The DIB section is top-down; the output surface is bottom-up.
        for (int y = 0; y < h; y++)
        {
            const uint8_t* src = (const uint8_t*)bits + (std::size_t)stride * y;
            uint8_t* dst = m_placeholder.get() + (std::size_t)stride * (h - 1 - y);
            std::memcpy(dst, src, (std::size_t)stride);
        }

        SelectObject(hdc, oldfont);
        DeleteObject(font);
        SelectObject(hdc, oldbm);
        DeleteObject(hbm);
        DeleteDC(hdc);
    }

    std::memcpy(pData, m_placeholder.get(), size);
}

HRESULT SoftcamStream::FillBuffer(IMediaSample *pms)
{
    CheckPointer(pms,E_POINTER);

    BYTE *pData;
    pms->GetPointer(&pData);
    long lDataLen = pms->GetSize();
    ZeroMemory(pData, (std::size_t)lDataLen);
    {
        if (auto fb = getParent()->getFrameBuffer())
        {
            bool active = fb->waitForNewFrame(m_frame_counter);
            fb->transferToDIB(pData, &m_frame_counter);

            if (!active)
            {
                // The sender has deactivated this stream and stopped sending frames.
                // Release the buffer; subsequent frames fall back to the placeholder.
                getParent()->releaseFrameBuffer();
                fillPlaceholder(pData);
            }
        }
        else
        {
            // No sender (GUI not running): show the placeholder.
            m_frame_counter = 0;
            Timer::sleep(0.100f);
            fillPlaceholder(pData);
        }

        CAutoLock lock(&m_critsec);
        CRefTime start = m_sample_time;
        m_sample_time += (LONG)m_interval_time_msec;
        pms->SetTime((REFERENCE_TIME*)&start,(REFERENCE_TIME*)&m_sample_time);
    }
    pms->SetSyncPoint(TRUE);
    //LOG("-> NOERROR\n");
    return NOERROR;
}


STDMETHODIMP SoftcamStream::Notify(IBaseFilter * pSender, Quality q)
{
    CAutoLock lock(&m_critsec);
    m_interval_time_msec = m_interval_time_msec * 1000 / (std::max)(q.Proportion, 1L);
    m_interval_time_msec = (std::min)((std::max)(m_interval_time_msec, 1L), 1000L);
    if (q.Late > 0) {
        m_sample_time += q.Late;
    }
    //LOG("-> NOERROR\n");
    return NOERROR;
}


HRESULT SoftcamStream::GetMediaType(CMediaType *pmt)
{
    CheckPointer(pmt,E_POINTER);

    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }

    VIDEOINFOHEADER *pvi = (VIDEOINFOHEADER*)pmt->AllocFormatBuffer(sizeof(VIDEOINFOHEADER));
    if (pvi == nullptr)
    {
        LOG("-> E_OUTOFMEMORY\n");
        return E_OUTOFMEMORY;
    }

    fillMediaType(pmt, getParent()->width(), getParent()->height(), getParent()->framerate());

    LOG("-> NOERROR\n");
    return NOERROR;
}

HRESULT SoftcamStream::DecideBufferSize(IMemAllocator *pAlloc,
                                        ALLOCATOR_PROPERTIES *pProperties)
{
    CheckPointer(pAlloc,E_POINTER);
    CheckPointer(pProperties,E_POINTER);

    CAutoLock lock(m_pFilter->pStateLock());
    HRESULT hr = NOERROR;

    VIDEOINFO *pvi = (VIDEOINFO *)m_mt.Format();
    pProperties->cBuffers = 1;
    pProperties->cbBuffer = (long)pvi->bmiHeader.biSizeImage;

    ALLOCATOR_PROPERTIES actual;
    hr = pAlloc->SetProperties(pProperties, &actual);
    if (FAILED(hr))
    {
        LOG("-> (FAILED)\n");
        return hr;
    }
    if (actual.cbBuffer < pProperties->cbBuffer)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    LOG("-> NOERROR\n");
    return NOERROR;
}

HRESULT SoftcamStream::OnThreadCreate()
{
    CAutoLock lock(&m_critsec);
    m_sample_time = 0;
    float framerate = getParent()->framerate();
    if (framerate <= 0.0f)
    {
        framerate = 60.0f;
    }
    framerate = (std::min)((std::max)(framerate, 1.0f), 1000.0f);
    m_interval_time_msec = (long)std::round(1000.0f / framerate);

    LOG("-> NOERROR\n");
    return NOERROR;
}

HRESULT SoftcamStream::Set(REFGUID guidPropSet, DWORD dwPropID,
                           LPVOID pInstanceData, DWORD cbInstanceData,
                           LPVOID pPropData, DWORD cbPropData)
{
    LOG("-> E_NOTIMPL\n");
    return E_NOTIMPL;
}

HRESULT SoftcamStream::Get(REFGUID guidPropSet, DWORD dwPropID,
                           LPVOID pInstanceData, DWORD cbInstanceData,
                           LPVOID pPropData, DWORD cbPropData, DWORD *pcbReturned)
{
    if (guidPropSet != AMPROPSETID_Pin)
    {
        LOG("-> E_PROP_SET_UNSUPPORTED\n");
        return E_PROP_SET_UNSUPPORTED;
    }
    if (dwPropID != AMPROPERTY_PIN_CATEGORY)
    {
        LOG("-> E_PROP_ID_UNSUPPORTED\n");
        return E_PROP_ID_UNSUPPORTED;
    }
    if (pPropData == nullptr && pcbReturned == nullptr)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (pcbReturned)
    {
        *pcbReturned = sizeof(GUID);
    }
    if (pPropData)
    {
        if (cbPropData < sizeof(GUID))
        {
            LOG("-> E_UNEXPECTED\n");
            return E_UNEXPECTED;
        }
        *(GUID*)pPropData = PIN_CATEGORY_CAPTURE;
    }
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT SoftcamStream::QuerySupported(REFGUID guidPropSet, DWORD dwPropID,
                              DWORD *pTypeSupport)
{
    if (guidPropSet != AMPROPSETID_Pin)
    {
        LOG("-> E_PROP_SET_UNSUPPORTED\n");
        return E_PROP_SET_UNSUPPORTED;
    }
    if (dwPropID != AMPROPERTY_PIN_CATEGORY)
    {
        LOG("-> E_PROP_ID_UNSUPPORTED\n");
        return E_PROP_ID_UNSUPPORTED;
    }
    if (pTypeSupport)
    {
        *pTypeSupport = KSPROPERTY_SUPPORT_GET;
    }
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT SoftcamStream::SetFormat(AM_MEDIA_TYPE *mt)
{
    return getParent()->SetFormat(mt);
}

HRESULT SoftcamStream::GetFormat(AM_MEDIA_TYPE **out_pmt)
{
    return getParent()->GetFormat(out_pmt);
}

HRESULT SoftcamStream::GetNumberOfCapabilities(int *out_count, int *out_size)
{
    return getParent()->GetNumberOfCapabilities(out_count, out_size);
}

HRESULT SoftcamStream::GetStreamCaps(int index, AM_MEDIA_TYPE **out_pmt, BYTE *out_scc)
{
    return getParent()->GetStreamCaps(index, out_pmt, out_scc);
}

Softcam* SoftcamStream::getParent()
{
    return static_cast<Softcam*>(m_pFilter);
}


} //namespace softcam
