// LibJpegWrap.cpp : Defines the functions for the static library.
//
#ifdef _WIN32
#define EXPORT __declspec(dllexport)
//#include <windows.h>
//#include <psapi.h>
//#include <string>
//#include <iostream>
//
//long long GetPrvMem() {
//    PROCESS_MEMORY_COUNTERS_EX pmc;
//    if (GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&pmc, sizeof(pmc))){
//        return pmc.PrivateUsage;
//    }
//    return 0;
//}
//
//// A class to manage memory allocations
//class AllocationBlock {
//public:
//    AllocationBlock(const std::string &location, int lineNumber)
//        : location_(location), lineNumber_(lineNumber), workingSet_(GetPrvMem()) {}
//
//    ~AllocationBlock() {
//        long long w = GetPrvMem();
//        long long d = w - workingSet_;
//        if (d > 16 * 1024) {
//            std::cout << "+" << d << "B of allocated: "<< w << "B in " << location_ << " at " << lineNumber_ << std::endl;
//        }
//        
//    }
//
//private:
//    std::string location_;
//    int lineNumber_;
//    long long workingSet_;
//};
//#define CHECK_ALLOCATION() AllocationBlock allocationBlock(__FILE__, __LINE__)

#endif

#ifndef _WIN32
#define EXPORT // Linux doesn't require a special export keyword
#endif

#include <iostream>
#include <cstdio>
#include <jpeglib.h>
#include <cstdlib>

typedef unsigned char byte;
typedef unsigned long ulong;

typedef struct {
    struct jpeg_destination_mgr pub; /* Public fields */
    byte* buffer;                 /* Start of the buffer */
    ulong buffer_size;              /* Buffer size */
    ulong data_size;                /* Final data size */
} memory_destination_mgr;

void init_destination(j_compress_ptr cinfo) {
    memory_destination_mgr* dest = (memory_destination_mgr*)cinfo->dest;
    dest->pub.next_output_byte = dest->buffer;
    dest->pub.free_in_buffer = dest->buffer_size;
    dest->data_size = 0; // No data written yet
}

boolean empty_output_buffer(j_compress_ptr cinfo) {
    // Handle buffer overflow. This could involve reallocating the buffer
    // and updating the relevant fields in the destination manager.
    // For simplicity, this example just prints an error and stops.
    
    std::cerr << "Buffer overflow in custom destination manager\n";
    return FALSE; // Causes the library to terminate with an error.
}

void term_destination(j_compress_ptr cinfo) {
    memory_destination_mgr* dest = (memory_destination_mgr*)cinfo->dest;
    dest->data_size = dest->buffer_size - dest->pub.free_in_buffer;
    // At this point, dest->data_size contains the size of the JPEG data.
}
memory_destination_mgr* jpeg_memory_dest(j_compress_ptr cinfo, byte* buffer, ulong size) {
    if (cinfo->dest == nullptr) { // Allocate memory for the custom manager if necessary
        cinfo->dest = (struct jpeg_destination_mgr*)
            (*cinfo->mem->alloc_small) ((j_common_ptr)cinfo, JPOOL_PERMANENT,
                sizeof(memory_destination_mgr));
        //std::cout << "\n\rAllocating " << sizeof(memory_destination_mgr) << "B for libjpeg buffer.\n\r";
        memory_destination_mgr* dest = (memory_destination_mgr*)cinfo->dest;
        dest->pub.init_destination = init_destination;
        dest->pub.empty_output_buffer = empty_output_buffer;
        dest->pub.term_destination = term_destination;
        dest->buffer = buffer;
        dest->buffer_size = size;
        dest->data_size = 0; // No data written yet
        return dest;
    }
    else 
    {
        memory_destination_mgr* dest = (memory_destination_mgr*)cinfo->dest;

        dest->buffer = buffer;
        dest->buffer_size = size;
        dest->data_size = 0; // No data written yet
        return dest;
    }
}

class YuvEncoder {
public:
   
	struct jpeg_compress_struct cinfo;
	struct jpeg_error_mgr jerr;

    YuvEncoder(const int width, const int height, const int quality, const int bufferSize)
    {
    	cinfo.err = jpeg_std_error(&jerr);
		jpeg_create_compress(&cinfo);
        cinfo.image_width = width;
        cinfo.image_height = height;
        cinfo.input_components = 3;
        cinfo.in_color_space = JCS_YCbCr;
        
        
        jpeg_set_defaults(&cinfo);
        jpeg_set_quality(&cinfo, quality, FALSE);
        
        cinfo.raw_data_in = TRUE; // Supply downsampled data
        cinfo.comp_info[0].h_samp_factor = 2;
        cinfo.comp_info[0].v_samp_factor = 2;
        cinfo.comp_info[1].h_samp_factor = 1;
        cinfo.comp_info[1].v_samp_factor = 1;
        cinfo.comp_info[2].h_samp_factor = 1;
        cinfo.comp_info[2].v_samp_factor = 1;
        //cinfo.dct_method = JDCT_FASTEST;
        jpeg_memory_dest(&cinfo, nullptr, bufferSize);
    }
    void SetQuality(int quality)
	{
		jpeg_set_quality(&cinfo, quality, FALSE);
	}
    // 0 - int
    // 1 - fast
    void SetMode(int mode)
    {
        if(mode == 0)
            cinfo.dct_method = JDCT_ISLOW;
        else 
            cinfo.dct_method = JDCT_FASTEST;
    }
    ulong Encode(byte* data, byte* dstBuffer, ulong dstBufferSize)
    {
        //CHECK_ALLOCATION();
        
        jpeg_memory_dest(&cinfo, dstBuffer, dstBufferSize);
        jpeg_start_compress(&cinfo, TRUE);
      
        auto width = cinfo.image_width;
        auto height = cinfo.image_height;

        // Calculate the sizes of the Y, U, and V planes
        size_t sizeY = width * height;
        size_t sizeU = sizeY / 4;
        //size_t sizeV = sizeU; // Same as sizeU

        // Split yuv420Data into Y, U, and V components
        byte* Y = data;
        byte* U = data + sizeY;
        byte* V = data + sizeY + sizeU;

        while (cinfo.next_scanline < cinfo.image_height) {
            JSAMPROW y[16], cb[8], cr[8];
            for (int i = 0; i < 16 && cinfo.next_scanline + i < cinfo.image_height; i++) {
                y[i] = &Y[(cinfo.next_scanline + i) * width];
                if (i % 2 == 0) {
                    cb[i / 2] = &U[((cinfo.next_scanline + i) / 2) * (width / 2)];
                    cr[i / 2] = &V[((cinfo.next_scanline + i) / 2) * (width / 2)];
                }
            }
            JSAMPARRAY planes[3] = { y, cb, cr };
            //{
                //CHECK_ALLOCATION();
            jpeg_write_raw_data(&cinfo, planes, 16);
            //}
        }
        
        jpeg_finish_compress(&cinfo);
        
        memory_destination_mgr* dest = (memory_destination_mgr*)cinfo.dest;
        return dest->data_size;
    }
    ~YuvEncoder()
    {
        jpeg_destroy_compress(&cinfo);
    }
};
typedef struct YuvEncoder YuvEncoder;

// Memory source manager for decompression
typedef struct {
    struct jpeg_source_mgr pub;
    const byte* buffer;
    ulong buffer_size;
} memory_source_mgr;

void init_source(j_decompress_ptr cinfo) {
    // No initialization needed
}

boolean fill_input_buffer(j_decompress_ptr cinfo) {
    // Should not be called since we provide all data upfront
    return FALSE;
}

void skip_input_data(j_decompress_ptr cinfo, long num_bytes) {
    memory_source_mgr* src = (memory_source_mgr*)cinfo->src;
    if (num_bytes > 0) {
        src->pub.next_input_byte += num_bytes;
        src->pub.bytes_in_buffer -= num_bytes;
    }
}

void term_source(j_decompress_ptr cinfo) {
    // No cleanup needed
}

void jpeg_memory_src(j_decompress_ptr cinfo, const byte* buffer, ulong size) {
    memory_source_mgr* src;

    if (cinfo->src == nullptr) {
        cinfo->src = (struct jpeg_source_mgr*)
            (*cinfo->mem->alloc_small)((j_common_ptr)cinfo, JPOOL_PERMANENT,
                sizeof(memory_source_mgr));
    }

    src = (memory_source_mgr*)cinfo->src;
    src->pub.init_source = init_source;
    src->pub.fill_input_buffer = fill_input_buffer;
    src->pub.skip_input_data = skip_input_data;
    src->pub.resync_to_restart = jpeg_resync_to_restart;
    src->pub.term_source = term_source;
    src->buffer = buffer;
    src->buffer_size = size;
    src->pub.next_input_byte = buffer;
    src->pub.bytes_in_buffer = size;
}

// Decoder result structure
typedef struct {
    int width;
    int height;
    int components;
    int colorSpace;
} DecodeInfo;

// Decode JPEG to grayscale (for HDR blending)
// Returns: bytes written to output, or 0 on error
// Output format: 8-bit grayscale, row-major
ulong DecodeToGray(const byte* jpegData, ulong jpegSize, byte* output, ulong outputSize, DecodeInfo* info) {
    struct jpeg_decompress_struct cinfo;
    struct jpeg_error_mgr jerr;

    cinfo.err = jpeg_std_error(&jerr);
    jpeg_create_decompress(&cinfo);

    jpeg_memory_src(&cinfo, jpegData, jpegSize);

    if (jpeg_read_header(&cinfo, TRUE) != JPEG_HEADER_OK) {
        jpeg_destroy_decompress(&cinfo);
        return 0;
    }

    // Request grayscale output
    cinfo.out_color_space = JCS_GRAYSCALE;

    jpeg_start_decompress(&cinfo);

    info->width = cinfo.output_width;
    info->height = cinfo.output_height;
    info->components = cinfo.output_components;
    info->colorSpace = cinfo.out_color_space;

    ulong rowStride = cinfo.output_width * cinfo.output_components;
    ulong totalSize = rowStride * cinfo.output_height;

    if (totalSize > outputSize) {
        jpeg_abort_decompress(&cinfo);
        jpeg_destroy_decompress(&cinfo);
        return 0;
    }

    while (cinfo.output_scanline < cinfo.output_height) {
        byte* rowPtr = output + cinfo.output_scanline * rowStride;
        jpeg_read_scanlines(&cinfo, &rowPtr, 1);
    }

    jpeg_finish_decompress(&cinfo);
    jpeg_destroy_decompress(&cinfo);

    return totalSize;
}

// Decode JPEG to I420 (YUV 4:2:0 planar) for HDR blending with color
// Returns: bytes written to output, or 0 on error
// Output format: Y plane (width*height), U plane (width*height/4), V plane (width*height/4)
ulong DecodeToI420(const byte* jpegData, ulong jpegSize, byte* output, ulong outputSize, DecodeInfo* info) {
    struct jpeg_decompress_struct cinfo;
    struct jpeg_error_mgr jerr;

    cinfo.err = jpeg_std_error(&jerr);
    jpeg_create_decompress(&cinfo);

    jpeg_memory_src(&cinfo, jpegData, jpegSize);

    if (jpeg_read_header(&cinfo, TRUE) != JPEG_HEADER_OK) {
        jpeg_destroy_decompress(&cinfo);
        return 0;
    }

    // Request raw YUV output (planar, subsampled)
    cinfo.raw_data_out = TRUE;
    cinfo.out_color_space = JCS_YCbCr;

    jpeg_start_decompress(&cinfo);

    int width = cinfo.output_width;
    int height = cinfo.output_height;

    info->width = width;
    info->height = height;
    info->components = 3;
    info->colorSpace = cinfo.out_color_space;

    // I420 size: Y (width*height) + U (width*height/4) + V (width*height/4)
    ulong sizeY = width * height;
    ulong sizeU = sizeY / 4;
    ulong sizeV = sizeU;
    ulong totalSize = sizeY + sizeU + sizeV;

    if (totalSize > outputSize) {
        jpeg_abort_decompress(&cinfo);
        jpeg_destroy_decompress(&cinfo);
        return 0;
    }

    byte* Y = output;
    byte* U = output + sizeY;
    byte* V = output + sizeY + sizeU;

    // Read raw data in MCU rows (typically 16 lines for Y, 8 for U/V with 4:2:0)
    JSAMPROW y_rows[16];
    JSAMPROW u_rows[8];
    JSAMPROW v_rows[8];
    JSAMPARRAY planes[3] = { y_rows, u_rows, v_rows };

    int y_stride = width;
    int uv_stride = width / 2;

    while (cinfo.output_scanline < cinfo.output_height) {
        int lines_to_read = cinfo.output_height - cinfo.output_scanline;
        if (lines_to_read > 16) lines_to_read = 16;

        for (int i = 0; i < 16; i++) {
            int y_line = cinfo.output_scanline + i;
            if (y_line < height) {
                y_rows[i] = Y + y_line * y_stride;
            } else {
                y_rows[i] = Y + (height - 1) * y_stride; // Pad with last line
            }

            if (i < 8) {
                int uv_line = (cinfo.output_scanline / 2) + i;
                if (uv_line < height / 2) {
                    u_rows[i] = U + uv_line * uv_stride;
                    v_rows[i] = V + uv_line * uv_stride;
                } else {
                    u_rows[i] = U + (height / 2 - 1) * uv_stride;
                    v_rows[i] = V + (height / 2 - 1) * uv_stride;
                }
            }
        }

        jpeg_read_raw_data(&cinfo, planes, 16);
    }

    jpeg_finish_decompress(&cinfo);
    jpeg_destroy_decompress(&cinfo);

    return totalSize;
}

// Encode grayscale (Gray8) to JPEG
// Returns: bytes written to output, or 0 on error
ulong EncodeGray8(const byte* grayData, int width, int height, int quality, byte* output, ulong outputSize) {
    struct jpeg_compress_struct cinfo;
    struct jpeg_error_mgr jerr;

    cinfo.err = jpeg_std_error(&jerr);
    jpeg_create_compress(&cinfo);

    // Set up memory destination
    memory_destination_mgr* dest = jpeg_memory_dest(&cinfo, output, outputSize);

    cinfo.image_width = width;
    cinfo.image_height = height;
    cinfo.input_components = 1;
    cinfo.in_color_space = JCS_GRAYSCALE;

    jpeg_set_defaults(&cinfo);
    jpeg_set_quality(&cinfo, quality, TRUE);

    jpeg_start_compress(&cinfo, TRUE);

    JSAMPROW row_pointer[1];
    int row_stride = width;

    while (cinfo.next_scanline < cinfo.image_height) {
        row_pointer[0] = (JSAMPROW)&grayData[cinfo.next_scanline * row_stride];
        jpeg_write_scanlines(&cinfo, row_pointer, 1);
    }

    jpeg_finish_compress(&cinfo);

    ulong dataSize = dest->data_size;
    jpeg_destroy_compress(&cinfo);

    return dataSize;
}

// Get JPEG dimensions without full decode
int GetJpegInfo(const byte* jpegData, ulong jpegSize, DecodeInfo* info) {
    struct jpeg_decompress_struct cinfo;
    struct jpeg_error_mgr jerr;

    cinfo.err = jpeg_std_error(&jerr);
    jpeg_create_decompress(&cinfo);

    jpeg_memory_src(&cinfo, jpegData, jpegSize);

    if (jpeg_read_header(&cinfo, TRUE) != JPEG_HEADER_OK) {
        jpeg_destroy_decompress(&cinfo);
        return 0;
    }

    info->width = cinfo.image_width;
    info->height = cinfo.image_height;
    info->components = cinfo.num_components;
    info->colorSpace = cinfo.jpeg_color_space;

    jpeg_destroy_decompress(&cinfo);
    return 1;
}

extern "C" {
    EXPORT YuvEncoder* Create(int width, int height, int quality, ulong size) {
        YuvEncoder* enc = new YuvEncoder(width, height, quality, size);
		return enc;
    }
    EXPORT ulong Encode(YuvEncoder* encoder, byte* data, byte* dstBuffer, ulong dstBufferSize) {
        return encoder->Encode(data, dstBuffer, dstBufferSize);
    }
    EXPORT void SetQuality(YuvEncoder* encoder, int quality) {
        encoder->SetQuality(quality);
    }
    EXPORT void SetMode(YuvEncoder* encoder, int mode) {
        encoder->SetMode(mode);
    }

    EXPORT void Close(YuvEncoder* encoder)
	{
        delete encoder;
    }

    // Decode functions
    EXPORT ulong DecodeJpegToGray(const byte* jpegData, ulong jpegSize, byte* output, ulong outputSize, DecodeInfo* info) {
        return DecodeToGray(jpegData, jpegSize, output, outputSize, info);
    }

    EXPORT int GetJpegImageInfo(const byte* jpegData, ulong jpegSize, DecodeInfo* info) {
        return GetJpegInfo(jpegData, jpegSize, info);
    }

    EXPORT ulong EncodeGray8ToJpeg(const byte* grayData, int width, int height, int quality, byte* output, ulong outputSize) {
        return EncodeGray8(grayData, width, height, quality, output, outputSize);
    }

    EXPORT ulong DecodeJpegToI420(const byte* jpegData, ulong jpegSize, byte* output, ulong outputSize, DecodeInfo* info) {
        return DecodeToI420(jpegData, jpegSize, output, outputSize, info);
    }
}