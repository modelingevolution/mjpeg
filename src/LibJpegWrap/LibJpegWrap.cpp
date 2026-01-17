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
}