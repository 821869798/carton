package main

import (
	"bytes"
	"encoding/binary"
	"errors"
	"flag"
	"fmt"
	"image"
	"image/draw"
	"image/png"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

var defaultSizes = []int{16, 32, 48, 64, 128, 256}

func main() {
	inputPath := flag.String("in", "", "input PNG path")
	outputPath := flag.String("out", "", "output ICO path")
	sizesArg := flag.String("sizes", "", "comma separated icon sizes, default: 16,32,48,64,128,256")
	flag.Parse()

	repoRoot, err := findRepoRoot()
	if err != nil {
		exitf("find repo root: %v", err)
	}

	if *inputPath == "" {
		*inputPath = filepath.Join(repoRoot, "src", "carton.GUI", "Assets", "carton_icon_full.png")
		if _, err := os.Stat(*inputPath); errors.Is(err, os.ErrNotExist) {
			*inputPath = filepath.Join(repoRoot, "src", "carton.GUI", "Assets", "carton_icon.png")
		}
	} else if !filepath.IsAbs(*inputPath) {
		*inputPath, err = filepath.Abs(*inputPath)
		if err != nil {
			exitf("resolve input path: %v", err)
		}
	}

	if *outputPath == "" {
		*outputPath = filepath.Join(repoRoot, "src", "carton.GUI", "Assets", "carton_icon.ico")
	} else if !filepath.IsAbs(*outputPath) {
		*outputPath, err = filepath.Abs(*outputPath)
		if err != nil {
			exitf("resolve output path: %v", err)
		}
	}

	sizes, err := parseSizes(*sizesArg)
	if err != nil {
		exitf("invalid sizes: %v", err)
	}

	srcImage, err := loadPNG(*inputPath)
	if err != nil {
		exitf("load png: %v", err)
	}

	frames, err := buildFrames(srcImage, sizes)
	if err != nil {
		exitf("build frames: %v", err)
	}

	if err := writeICO(*outputPath, frames); err != nil {
		exitf("write ico: %v", err)
	}

	fmt.Printf("wrote %s from %s with sizes %v\n", *outputPath, *inputPath, sizes)
}

func findRepoRoot() (string, error) {
	dir, err := os.Getwd()
	if err != nil {
		return "", err
	}

	for {
		if fileExists(filepath.Join(dir, ".git")) || fileExists(filepath.Join(dir, "carton.slnx")) {
			return dir, nil
		}

		parent := filepath.Dir(dir)
		if parent == dir {
			return "", fmt.Errorf("could not locate repository root from %s", dir)
		}
		dir = parent
	}
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func parseSizes(sizesArg string) ([]int, error) {
	if strings.TrimSpace(sizesArg) == "" {
		return append([]int(nil), defaultSizes...), nil
	}

	parts := strings.Split(sizesArg, ",")
	sizes := make([]int, 0, len(parts))
	seen := make(map[int]struct{}, len(parts))
	for _, part := range parts {
		value, err := strconv.Atoi(strings.TrimSpace(part))
		if err != nil {
			return nil, fmt.Errorf("parse %q: %w", part, err)
		}
		if value < 1 || value > 256 {
			return nil, fmt.Errorf("size %d out of range, expected 1-256", value)
		}
		if _, ok := seen[value]; ok {
			return nil, fmt.Errorf("duplicate size %d", value)
		}
		seen[value] = struct{}{}
		sizes = append(sizes, value)
	}

	return sizes, nil
}

func loadPNG(path string) (*image.NRGBA, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	img, err := png.Decode(file)
	if err != nil {
		return nil, err
	}

	bounds := img.Bounds()
	if bounds.Empty() {
		return nil, fmt.Errorf("empty image")
	}

	dst := image.NewNRGBA(image.Rect(0, 0, bounds.Dx(), bounds.Dy()))
	draw.Draw(dst, dst.Bounds(), img, bounds.Min, draw.Src)
	return dst, nil
}

type icoFrame struct {
	size int
	data []byte
}

func buildFrames(src *image.NRGBA, sizes []int) ([]icoFrame, error) {
	frames := make([]icoFrame, 0, len(sizes))
	for _, size := range sizes {
		scaled := resizeBilinearNRGBA(src, size, size)

		var buf bytes.Buffer
		encoder := png.Encoder{CompressionLevel: png.BestCompression}
		if err := encoder.Encode(&buf, scaled); err != nil {
			return nil, fmt.Errorf("encode %dx%d png: %w", size, size, err)
		}

		frames = append(frames, icoFrame{
			size: size,
			data: buf.Bytes(),
		})
	}

	return frames, nil
}

func writeICO(path string, frames []icoFrame) error {
	if len(frames) == 0 {
		return fmt.Errorf("no frames")
	}

	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}

	file, err := os.Create(path)
	if err != nil {
		return err
	}
	defer file.Close()

	if err := binary.Write(file, binary.LittleEndian, uint16(0)); err != nil {
		return err
	}
	if err := binary.Write(file, binary.LittleEndian, uint16(1)); err != nil {
		return err
	}
	if err := binary.Write(file, binary.LittleEndian, uint16(len(frames))); err != nil {
		return err
	}

	offset := 6 + len(frames)*16
	for _, frame := range frames {
		widthByte := byte(frame.size)
		heightByte := byte(frame.size)
		if frame.size == 256 {
			widthByte = 0
			heightByte = 0
		}

		if err := binary.Write(file, binary.LittleEndian, widthByte); err != nil {
			return err
		}
		if err := binary.Write(file, binary.LittleEndian, heightByte); err != nil {
			return err
		}
		if err := binary.Write(file, binary.LittleEndian, byte(0)); err != nil {
			return err
		}
		if err := binary.Write(file, binary.LittleEndian, byte(0)); err != nil {
			return err
		}
		if err := binary.Write(file, binary.LittleEndian, uint16(1)); err != nil {
			return err
		}
		if err := binary.Write(file, binary.LittleEndian, uint16(32)); err != nil {
			return err
		}
		if err := binary.Write(file, binary.LittleEndian, uint32(len(frame.data))); err != nil {
			return err
		}
		if err := binary.Write(file, binary.LittleEndian, uint32(offset)); err != nil {
			return err
		}

		offset += len(frame.data)
	}

	for _, frame := range frames {
		if _, err := file.Write(frame.data); err != nil {
			return err
		}
	}

	return nil
}

func resizeBilinearNRGBA(src *image.NRGBA, width, height int) *image.NRGBA {
	dst := image.NewNRGBA(image.Rect(0, 0, width, height))
	srcBounds := src.Bounds()
	srcWidth := srcBounds.Dx()
	srcHeight := srcBounds.Dy()

	if srcWidth == width && srcHeight == height {
		copy(dst.Pix, src.Pix)
		return dst
	}

	scaleX := float64(srcWidth) / float64(width)
	scaleY := float64(srcHeight) / float64(height)

	for y := 0; y < height; y++ {
		srcY := (float64(y)+0.5)*scaleY - 0.5
		y0, y1, wy := interpolateCoords(srcY, srcHeight)

		for x := 0; x < width; x++ {
			srcX := (float64(x)+0.5)*scaleX - 0.5
			x0, x1, wx := interpolateCoords(srcX, srcWidth)

			c00 := readPixelPremultiplied(src, x0, y0)
			c10 := readPixelPremultiplied(src, x1, y0)
			c01 := readPixelPremultiplied(src, x0, y1)
			c11 := readPixelPremultiplied(src, x1, y1)

			top := mixPixel(c00, c10, wx)
			bottom := mixPixel(c01, c11, wx)
			pixel := mixPixel(top, bottom, wy)

			writePixel(dst, x, y, pixel)
		}
	}

	return dst
}

type premultipliedPixel struct {
	r float64
	g float64
	b float64
	a float64
}

func interpolateCoords(coord float64, size int) (int, int, float64) {
	if size <= 1 {
		return 0, 0, 0
	}

	if coord <= 0 {
		return 0, 0, 0
	}
	maxCoord := float64(size - 1)
	if coord >= maxCoord {
		last := size - 1
		return last, last, 0
	}

	base := int(coord)
	next := base + 1
	weight := coord - float64(base)
	return base, next, weight
}

func readPixelPremultiplied(img *image.NRGBA, x, y int) premultipliedPixel {
	idx := img.PixOffset(x, y)
	r := float64(img.Pix[idx+0])
	g := float64(img.Pix[idx+1])
	b := float64(img.Pix[idx+2])
	a := float64(img.Pix[idx+3]) / 255.0

	return premultipliedPixel{
		r: r * a,
		g: g * a,
		b: b * a,
		a: a * 255.0,
	}
}

func mixPixel(a, b premultipliedPixel, weight float64) premultipliedPixel {
	inv := 1.0 - weight
	return premultipliedPixel{
		r: a.r*inv + b.r*weight,
		g: a.g*inv + b.g*weight,
		b: a.b*inv + b.b*weight,
		a: a.a*inv + b.a*weight,
	}
}

func writePixel(img *image.NRGBA, x, y int, pixel premultipliedPixel) {
	idx := img.PixOffset(x, y)
	alpha := clamp(pixel.a)
	if alpha == 0 {
		img.Pix[idx+0] = 0
		img.Pix[idx+1] = 0
		img.Pix[idx+2] = 0
		img.Pix[idx+3] = 0
		return
	}

	a := float64(alpha) / 255.0
	img.Pix[idx+0] = clamp(pixel.r / a)
	img.Pix[idx+1] = clamp(pixel.g / a)
	img.Pix[idx+2] = clamp(pixel.b / a)
	img.Pix[idx+3] = alpha
}

func clamp(value float64) byte {
	switch {
	case value <= 0:
		return 0
	case value >= 255:
		return 255
	default:
		return byte(value + 0.5)
	}
}

func exitf(format string, args ...any) {
	fmt.Fprintf(os.Stderr, format+"\n", args...)
	os.Exit(1)
}
