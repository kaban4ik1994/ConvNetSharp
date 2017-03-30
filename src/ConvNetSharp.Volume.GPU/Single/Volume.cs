﻿using System;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using ManagedCuda.CudaDNN;

namespace ConvNetSharp.Volume.GPU.Single
{
    public class Volume : Volume<float>, IDisposable
    {
        private readonly GpuContext _context;
        private readonly VolumeStorage _volumeStorage;

        public Volume(VolumeStorage storage) : base(new VolumeStorage(storage, storage.Shape))
        {
            this._context = storage.Context;
            this._volumeStorage = this.Storage as VolumeStorage;
        }

        public Volume(float[] array, Shape shape) : base(new VolumeStorage(array, shape, GpuContext.Default))
        {
            this._context = GpuContext.Default;
            this._volumeStorage = this.Storage as VolumeStorage;
        }

        public Volume(float[] array, Shape shape, GpuContext context) : base(new VolumeStorage(array, shape, context))
        {
            this._context = context;
            this._volumeStorage = this.Storage as VolumeStorage;
        }

        public void Dispose()
        {
            this._volumeStorage?.Dispose();
        }

        private void DoActivation(Volume<float> result, cudnnActivationMode mode)
        {
            var resultStorage = result.Storage as VolumeStorage;
            if (resultStorage == null)
            {
                throw new ArgumentException($"{nameof(result)} storage should be VolumeStorage", nameof(result));
            }

            // Copy to device if not already done
            this._volumeStorage.CopyToDevice();
            resultStorage.CopyToDevice();

            // Synchro
            this._context.DefaultStream.Synchronize();

            // Relu
            using (var activationDesc = new ActivationDescriptor())
            using (var srcDesc = new TensorDescriptor())
            using (var resultDesc = new TensorDescriptor())
            {
                var n = result.Shape.GetDimension(3);
                var c = result.Shape.GetDimension(2);
                var h = result.Shape.GetDimension(1);
                var w = result.Shape.GetDimension(0);

                srcDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                resultDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                activationDesc.SetActivationDescriptor(mode, cudnnNanPropagation.NotPropagateNan, 0.0);

                this._context.CudnnContext.ActivationForward(activationDesc, 1.0f, srcDesc, this._volumeStorage.DeviceBuffer, 0.0f,
                    resultDesc, resultStorage.DeviceBuffer);
            }

            resultStorage.CopiedToDevice = true;
        }

        private void DoActivationGradient(Volume<float> input, Volume<float> outputGradient,
            Volume<float> inputGradient, cudnnActivationMode mode)
        {
            var inputStorage = input.Storage as VolumeStorage;
            var inputGradientStorage = inputGradient.Storage as VolumeStorage;
            var outputStorage = this._volumeStorage;
            var outputGradientStorage = outputGradient.Storage as VolumeStorage;

            // Copy to device if not already done
            outputStorage.CopyToDevice();
            outputGradientStorage.CopyToDevice();
            inputGradientStorage.CopyToDevice();

            // Synchro
            this._context.DefaultStream.Synchronize();

            using (var activationDesc = new ActivationDescriptor())
            using (var srcDesc = new TensorDescriptor())
            using (var srcDiffDesc = new TensorDescriptor())
            using (var destDesc = new TensorDescriptor())
            using (var destDiffDesc = new TensorDescriptor())
            {
                var n = this.Shape.GetDimension(3);
                var c = this.Shape.GetDimension(2);
                var h = this.Shape.GetDimension(1);
                var w = this.Shape.GetDimension(0);

                srcDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                srcDiffDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                destDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                destDiffDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);

                activationDesc.SetActivationDescriptor(mode, cudnnNanPropagation.NotPropagateNan,
                    0.0);

                this._context.CudnnContext.ActivationBackward(activationDesc, 1.0f,
                    srcDesc, outputStorage.DeviceBuffer,
                    srcDiffDesc, outputGradientStorage.DeviceBuffer,
                    destDesc, inputStorage.DeviceBuffer,
                    0.0f,
                    destDiffDesc, inputGradientStorage.DeviceBuffer);
            }

            inputGradientStorage.CopiedToDevice = true;
        }

        public override void DoAdd(Volume<float> other, Volume<float> result)
        {
            var otherStorage = other.Storage as VolumeStorage;
            var resultStorage = result.Storage as VolumeStorage;

            if (otherStorage == null)
            {
                throw new ArgumentException($"{nameof(other)} storage should be VolumeStorage", nameof(other));
            }

            if (resultStorage == null)
            {
                throw new ArgumentException($"{nameof(result)} storage should be VolumeStorage", nameof(result));
            }

            // Copy to device if not already done
            this._volumeStorage.CopyToDevice();
            otherStorage.CopyToDevice();
            resultStorage.CopyToDevice();

            // result = this
            DriverAPINativeMethods.SynchronousMemcpy_v2.cuMemcpy(resultStorage.DeviceBuffer.DevicePointer,
                this._volumeStorage.DeviceBuffer.DevicePointer, this.Shape.TotalLength * sizeof(float));
            resultStorage.CopiedToDevice = true;

            // Synchro
            this._context.DefaultStream.Synchronize();

            // Add tensors
            using (var biasDesc = new TensorDescriptor())
            using (var srcDesc = new TensorDescriptor())
            {
                srcDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    this.Shape.GetDimension(3),
                    this.Shape.GetDimension(2),
                    this.Shape.GetDimension(1),
                    this.Shape.GetDimension(0));

                biasDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    other.Shape.GetDimension(3),
                    other.Shape.GetDimension(2),
                    other.Shape.GetDimension(1),
                    other.Shape.GetDimension(0));

                this._context.CudnnContext.AddTensor(1.0f,
                    biasDesc, otherStorage.DeviceBuffer, 1.0f,
                    srcDesc, resultStorage.DeviceBuffer);
            }
        }

        protected override void DoBiasGradient(Volume<float> biasGradient)
        {
            var outputGradientStorage = this._volumeStorage;
            var biasGradientStorage = biasGradient.Storage as VolumeStorage;

            // Copy to device if not already done
            outputGradientStorage.CopyToDevice();
            biasGradientStorage.CopyToDevice();

            using (var dOutputDesc = new TensorDescriptor())
            using (var dBiasDesc = new TensorDescriptor())
            {
                dOutputDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    this.Shape.GetDimension(3),
                    this.Shape.GetDimension(2),
                    this.Shape.GetDimension(1),
                    this.Shape.GetDimension(0));

                dBiasDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    biasGradient.Shape.GetDimension(3),
                    biasGradient.Shape.GetDimension(2),
                    biasGradient.Shape.GetDimension(1),
                    biasGradient.Shape.GetDimension(0));

                // bias
                this._context.CudnnContext.ConvolutionBackwardBias(1.0f, dOutputDesc, outputGradientStorage.DeviceBuffer, 0.0f,
                    dBiasDesc, biasGradientStorage.DeviceBuffer);
            }

            biasGradientStorage.CopiedToDevice = true;
        }

        public override void DoConvolution(Volume<float> filters, int pad, int stride, Volume<float> result)
        {
            var resultStorage = result.Storage as VolumeStorage;
            if (resultStorage == null)
            {
                throw new ArgumentException($"{nameof(result)} storage should be VolumeStorage", nameof(result));
            }

            var inputStorage = this._volumeStorage;
            var filterStorage = filters.Storage as VolumeStorage;

            // Copy to device if not already done
            inputStorage.CopyToDevice();
            filterStorage.CopyToDevice();
            resultStorage.CopyToDevice();

            // Synchro
            this._context.DefaultStream.Synchronize();

            using (var dataDesc = new TensorDescriptor())
            using (var filterDesc = new FilterDescriptor())
            using (var outputDesc = new TensorDescriptor())
            using (var convolutionDesc = new ConvolutionDescriptor())
            {
                convolutionDesc.SetConvolution2dDescriptor(pad, pad, stride, stride, 1, 1,
                    cudnnConvolutionMode.CrossCorrelation, cudnnDataType.Float);

                dataDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    this.Shape.GetDimension(3),
                    this.Shape.GetDimension(2),
                    this.Shape.GetDimension(1),
                    this.Shape.GetDimension(0));

                filterDesc.SetFilter4dDescriptor(cudnnDataType.Float, cudnnTensorFormat.NCHW,
                    filters.Shape.GetDimension(3),
                    filters.Shape.GetDimension(2),
                    filters.Shape.GetDimension(1),
                    filters.Shape.GetDimension(0));

                outputDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    result.Shape.GetDimension(3),
                    result.Shape.GetDimension(2),
                    result.Shape.GetDimension(1),
                    result.Shape.GetDimension(0));

                var algo = this._context.CudnnContext.GetConvolutionForwardAlgorithm(
                    dataDesc, filterDesc,
                    convolutionDesc, outputDesc,
                    cudnnConvolutionFwdPreference.PreferFastest, IntPtr.Zero);

                var workspaceSize = this._context.CudnnContext.GetConvolutionForwardWorkspaceSize(
                    dataDesc, filterDesc,
                    convolutionDesc, outputDesc, algo);
                workspaceSize = workspaceSize == 0 ? new SizeT(1) : workspaceSize;

                if (this._volumeStorage.ConvolutionStorage == null || this._volumeStorage.ConvolutionStorage.Size != workspaceSize)
                {
                    this._volumeStorage.ConvolutionStorage = new CudaDeviceVariable<byte>(workspaceSize);
                }

                this._context.CudnnContext.ConvolutionForward(1.0f,
                    dataDesc, inputStorage.DeviceBuffer,
                    filterDesc, filterStorage.DeviceBuffer,
                    convolutionDesc, algo, this._volumeStorage.ConvolutionStorage, 0.0f,
                    outputDesc, resultStorage.DeviceBuffer);
            }

            resultStorage.CopiedToDevice = true;
        }

        protected override void DoConvolutionGradient(Volume<float> filters, Volume<float> outputGradients,
            Volume<float> inputGradient, Volume<float> filterGradient, int pad,
            int stride)
        {
            var inputStorage = this._volumeStorage;
            var outputGradientStorage = outputGradients.Storage as VolumeStorage;
            var filterstorage = filters.Storage as VolumeStorage;
            var inputGradientStorage = inputGradient.Storage as VolumeStorage;
            var filterGradientStorage = filterGradient.Storage as VolumeStorage;

            // Copy to device if not already done
            inputStorage.CopyToDevice();
            outputGradientStorage.CopyToDevice();
            filterstorage.CopyToDevice();
            inputGradientStorage.CopyToDevice();
            filterGradientStorage.CopyToDevice();

            using (var dataDesc = new TensorDescriptor())
            using (var filterDesc = new FilterDescriptor())
            using (var dDataDesc = new TensorDescriptor())
            using (var dOutputDesc = new TensorDescriptor())
            using (var dfilterDesc = new FilterDescriptor())
            using (var convolutionDesc = new ConvolutionDescriptor())
            {
                convolutionDesc.SetConvolution2dDescriptor(pad, pad, stride, stride, 1, 1,
                    cudnnConvolutionMode.CrossCorrelation, cudnnDataType.Float);

                dataDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    this.Shape.GetDimension(3),
                    this.Shape.GetDimension(2),
                    this.Shape.GetDimension(1),
                    this.Shape.GetDimension(0));

                dDataDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    this.Shape.GetDimension(3),
                    this.Shape.GetDimension(2),
                    this.Shape.GetDimension(1),
                    this.Shape.GetDimension(0));

                dOutputDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    outputGradients.Shape.GetDimension(3),
                    outputGradients.Shape.GetDimension(2),
                    outputGradients.Shape.GetDimension(1),
                    outputGradients.Shape.GetDimension(0));

                filterDesc.SetFilter4dDescriptor(cudnnDataType.Float, cudnnTensorFormat.NCHW,
                    filters.Shape.GetDimension(3),
                    filters.Shape.GetDimension(2),
                    filters.Shape.GetDimension(1),
                    filters.Shape.GetDimension(0));

                dfilterDesc.SetFilter4dDescriptor(cudnnDataType.Float, cudnnTensorFormat.NCHW,
                    filters.Shape.GetDimension(3),
                    filters.Shape.GetDimension(2),
                    filters.Shape.GetDimension(1),
                    filters.Shape.GetDimension(0));

                var filterAlgo = this._context.CudnnContext.GetConvolutionBackwardFilterAlgorithm(dataDesc, dOutputDesc,
                    convolutionDesc, dfilterDesc, cudnnConvolutionBwdFilterPreference.PreferFastest, IntPtr.Zero);
                var filterWorkspaceSize = this._context.CudnnContext.GetConvolutionBackwardFilterWorkspaceSize(dataDesc,
                    dOutputDesc, convolutionDesc, dfilterDesc, filterAlgo);
                filterWorkspaceSize = filterWorkspaceSize == 0 ? new SizeT(1) : filterWorkspaceSize;

                var dataAlgo = this._context.CudnnContext.GetConvolutionBackwardDataAlgorithm(filterDesc, dOutputDesc,
                    convolutionDesc, dDataDesc, cudnnConvolutionBwdDataPreference.PreferFastest, IntPtr.Zero);
                var dataWorkspaceSize = this._context.CudnnContext.GetConvolutionBackwardDataWorkspaceSize(dfilterDesc,
                    dOutputDesc, convolutionDesc, dDataDesc, dataAlgo);
                dataWorkspaceSize = dataWorkspaceSize == 0 ? new SizeT(1) : dataWorkspaceSize;

                // filter
                if (this._volumeStorage.ConvolutionBackwardFilterStorage == null || this._volumeStorage.ConvolutionBackwardFilterStorage.Size != filterWorkspaceSize)
                {
                    this._volumeStorage.ConvolutionBackwardFilterStorage = new CudaDeviceVariable<byte>(filterWorkspaceSize);
                }
                this._context.CudnnContext.ConvolutionBackwardFilter(1.0f, dataDesc, inputStorage.DeviceBuffer, dOutputDesc,
                    outputGradientStorage.DeviceBuffer, convolutionDesc, filterAlgo,
                    this._volumeStorage.ConvolutionBackwardFilterStorage, 0.0f, dfilterDesc,
                    filterGradientStorage.DeviceBuffer);

                // data
                if (this._volumeStorage.ConvolutionBackwardStorage == null || this._volumeStorage.ConvolutionBackwardStorage.Size != dataWorkspaceSize)
                {
                    this._volumeStorage.ConvolutionBackwardStorage = new CudaDeviceVariable<byte>(dataWorkspaceSize);
                }
                this._context.CudnnContext.ConvolutionBackwardData(1.0f, filterDesc, filterstorage.DeviceBuffer, dOutputDesc,
                    outputGradientStorage.DeviceBuffer, convolutionDesc, 0.0f, dataAlgo,
                    this._volumeStorage.ConvolutionBackwardStorage, dDataDesc,
                    inputGradientStorage.DeviceBuffer);
            }

            filterGradientStorage.CopiedToDevice = true;
            inputGradientStorage.CopiedToDevice = true;
        }

        protected override void DoMultiply(Volume<float> result, float factor)
        {
            var resultStorage = result.Storage as VolumeStorage;
            if (resultStorage == null)
            {
                throw new ArgumentException($"{nameof(result)} storage should be VolumeStorage", nameof(result));
            }

            // Copy to device if not already done
            this._volumeStorage.CopyToDevice();
            resultStorage.CopyToDevice();

            // result = this
            DriverAPINativeMethods.SynchronousMemcpy_v2.cuMemcpy(resultStorage.DeviceBuffer.DevicePointer,
                this._volumeStorage.DeviceBuffer.DevicePointer, this.Shape.TotalLength * sizeof(float));
            resultStorage.CopiedToDevice = true;

            // Synchro
            this._context.DefaultStream.Synchronize();

            // Add tensors
            using (var srcDesc = new TensorDescriptor())
            {
                var n = result.Shape.GetDimension(3);
                var c = result.Shape.GetDimension(2);
                var h = result.Shape.GetDimension(1);
                var w = result.Shape.GetDimension(0);

                srcDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);

                this._context.CudnnContext.ScaleTensor(srcDesc, resultStorage.DeviceBuffer, factor);
            }
        }

        protected override void DoNegate(Volume<float> result)
        {
            DoMultiply(result, -1.0f);
        }

        public override void DoPool(Volume<float> result, int windowWidth, int windowHeight,
            int horizontalPad, int verticalPad, int horizontalStride, int verticalStride)
        {
            var resultStorage = result.Storage as VolumeStorage;
            if (resultStorage == null)
            {
                throw new ArgumentException($"{nameof(result)} storage should be VolumeStorage", nameof(result));
            }

            // Copy to device if not already done
            this._volumeStorage.CopyToDevice();
            resultStorage.CopyToDevice();

            // Synchro
            this._context.DefaultStream.Synchronize();

            // Relu
            using (var poolingDesc = new PoolingDescriptor())
            using (var srcDesc = new TensorDescriptor())
            using (var resultDesc = new TensorDescriptor())
            {
                srcDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    this.Shape.GetDimension(3), this.Shape.GetDimension(2),
                    this.Shape.GetDimension(1), this.Shape.GetDimension(0));

                resultDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    result.Shape.GetDimension(3), result.Shape.GetDimension(2),
                    result.Shape.GetDimension(1), result.Shape.GetDimension(0));

                poolingDesc.SetPooling2dDescriptor(cudnnPoolingMode.Max, cudnnNanPropagation.NotPropagateNan,
                    windowHeight, windowWidth,
                    verticalPad, horizontalPad, verticalStride, horizontalStride);

                this._context.CudnnContext.PoolingForward(poolingDesc, 1.0f, srcDesc, this._volumeStorage.DeviceBuffer, 0.0f,
                    resultDesc, resultStorage.DeviceBuffer);
            }

            resultStorage.CopiedToDevice = true;
        }

        public override void DoPoolGradient(Volume<float> input, Volume<float> outputGradient,
            Volume<float> inputGradient, int windowWidth, int windowHeight,
            int horizontalPad, int verticalPad, int horizontalStride, int verticalStride)
        {
            var inputStorage = input.Storage as VolumeStorage;
            var inputGradientStorage = inputGradient.Storage as VolumeStorage;
            var outputStorage = this._volumeStorage;
            var outputGradientStorage = outputGradient.Storage as VolumeStorage;

            // Copy to device if not already done
            //outputStorage.CopyToDevice();
            outputGradientStorage.CopyToDevice();
            inputStorage.CopyToDevice();
            inputGradientStorage.CopyToDevice();

            // Synchro
            this._context.DefaultStream.Synchronize();

            using (var poolingDesc = new PoolingDescriptor())
            using (var srcDesc = new TensorDescriptor())
            using (var srcDiffDesc = new TensorDescriptor())
            using (var destDesc = new TensorDescriptor())
            using (var destDiffDesc = new TensorDescriptor())
            {
                var n = this.Shape.GetDimension(3);
                var c = this.Shape.GetDimension(2);
                var h = this.Shape.GetDimension(1);
                var w = this.Shape.GetDimension(0);

                srcDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                srcDiffDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);

                destDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    inputStorage.Shape.GetDimension(3), inputStorage.Shape.GetDimension(2),
                    inputStorage.Shape.GetDimension(1), inputStorage.Shape.GetDimension(0));
                destDiffDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float,
                    inputStorage.Shape.GetDimension(3), inputStorage.Shape.GetDimension(2),
                    inputStorage.Shape.GetDimension(1), inputStorage.Shape.GetDimension(0));

                poolingDesc.SetPooling2dDescriptor(cudnnPoolingMode.Max, cudnnNanPropagation.NotPropagateNan,
                    windowHeight, windowWidth,
                    verticalPad, horizontalPad, verticalStride, horizontalStride);

                this._context.CudnnContext.PoolingBackward(poolingDesc, 1.0f,
                    srcDesc, outputStorage.DeviceBuffer,
                    srcDiffDesc, outputGradientStorage.DeviceBuffer,
                    destDesc, inputStorage.DeviceBuffer,
                    0.0f,
                    destDiffDesc, inputGradientStorage.DeviceBuffer);
            }

            inputGradientStorage.CopiedToDevice = true;
        }

        public override void DoRelu(Volume<float> result)
        {
            DoActivation(result, cudnnActivationMode.Relu);
        }

        public override void DoReluGradient(Volume<float> input, Volume<float> outputGradient,
            Volume<float> inputGradient)
        {
            DoActivationGradient(input, outputGradient, inputGradient, cudnnActivationMode.Relu);
        }


        public override void DoSigmoid(Volume<float> result)
        {
            DoActivation(result, cudnnActivationMode.Sigmoid);
        }

        public override void DoSigmoidGradient(Volume<float> input, Volume<float> outputGradient,
            Volume<float> inputGradient)
        {
            DoActivationGradient(input, outputGradient, inputGradient, cudnnActivationMode.Sigmoid);
        }

        public override void DoSoftMax(Volume<float> output)
        {
            var inputStorage = this._volumeStorage;
            var outputStorage = output.Storage as VolumeStorage;

            // Copy to device if not already done
            inputStorage.CopyToDevice();
            outputStorage.CopyToDevice();

            using (var srcDesc = new TensorDescriptor())
            using (var destDesc = new TensorDescriptor())
            {
                var n = this.Shape.GetDimension(3);
                var c = this.Shape.GetDimension(2);
                var h = this.Shape.GetDimension(1);
                var w = this.Shape.GetDimension(0);

                srcDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                destDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);

                this._context.CudnnContext.SoftmaxForward(cudnnSoftmaxAlgorithm.Accurate, cudnnSoftmaxMode.Channel, 1.0f,
                    srcDesc, inputStorage.DeviceBuffer, 0.0f,
                    destDesc, outputStorage.DeviceBuffer);
            }

            outputStorage.CopiedToDevice = true;
        }

        public override void DoSoftMaxGradient(Volume<float> outputGradient, Volume<float> inputGradient)
        {
            var inputGradientStorage = inputGradient.Storage as VolumeStorage;
            var outputStorage = this._volumeStorage;
            var outputGradientStorage = outputGradient.Storage as VolumeStorage;

            // Copy to device if not already done
            outputStorage.CopyToDevice();
            outputGradientStorage.CopyToDevice();
            inputGradientStorage.CopyToDevice();

            // Synchro
            this._context.DefaultStream.Synchronize();

            using (var activationDesc = new ActivationDescriptor())
            using (var srcDesc = new TensorDescriptor())
            using (var srcDiffDesc = new TensorDescriptor())
            using (var destDiffDesc = new TensorDescriptor())
            {
                var n = this.Shape.GetDimension(3);
                var c = this.Shape.GetDimension(2);
                var h = this.Shape.GetDimension(1);
                var w = this.Shape.GetDimension(0);

                srcDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                srcDiffDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                destDiffDesc.SetTensor4dDescriptor(cudnnTensorFormat.NCHW, cudnnDataType.Float, n, c, h, w);
                activationDesc.SetActivationDescriptor(cudnnActivationMode.Relu, cudnnNanPropagation.PropagateNan, 0.0);

                this._context.CudnnContext.SoftmaxBackward(cudnnSoftmaxAlgorithm.Accurate, cudnnSoftmaxMode.Channel, 1.0f,
                    srcDesc, outputStorage.DeviceBuffer,
                    srcDiffDesc, outputGradientStorage.DeviceBuffer,
                    0.0f,
                    destDiffDesc, inputGradientStorage.DeviceBuffer);

                inputGradientStorage.CopiedToDevice = true;
            }
        }

        public override void DoTanh(Volume<float> result)
        {
            DoActivation(result, cudnnActivationMode.Tanh);
        }

        public override void DoTanhGradient(Volume<float> input, Volume<float> outputGradient,
            Volume<float> inputGradient)
        {
            DoActivationGradient(input, outputGradient, inputGradient, cudnnActivationMode.Tanh);
        }
    }
}