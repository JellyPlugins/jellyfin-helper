using System;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <content>Complete best-epoch/restart checkpoints and coordinate-covariant input clamps.</content>
public sealed partial class NeuralScoringStrategy
{
    private readonly int _inputSize = CandidateFeatures.FeatureCount;
    private double[] _inputWeightLimits = Enumerable.Repeat(WeightClamp, CandidateFeatures.FeatureCount).ToArray();
    private double[] _inputBiasLimits = Enumerable.Repeat(WeightClamp, Hidden1Size).ToArray();

    internal (int Version, int InputCount)? GetSchemaInfo()
    {
        try
        {
            _rwLock.EnterReadLock();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        try
        {
            return (CurrentWeightsVersion, _inputSize);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>Captures immutable prediction coordinates and their matching continuing optimizer under one lock.</summary>
    /// <returns>A detached complete checkpoint; prediction-only snapshots do not retain Adam arrays.</returns>
    internal NeuralWeightsData CaptureTrainingCheckpoint()
    {
        try
        {
            _rwLock.EnterReadLock();
            var data = CaptureParametersLocked();
            data.TrainingState = _mWIH is null ? null : CaptureOptimizerState();
            return data;
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    private NeuralWeightsData CaptureParametersLocked() => new PredictionSnapshot(new NeuralWeightsData
    {
        WeightsIH = _weightsIH, BiasH1 = _biasH1, WeightsH1H2 = _weightsH1H2, BiasH2 = _biasH2,
        WeightsH2H3 = _weightsH2H3, BiasH3 = _biasH3, WeightsH3H4 = _weightsH3H4, BiasH4 = _biasH4,
        WeightsH4O = _weightsH4O, BiasOutput = _biasOutput, FeatureMeans = _featureMeans,
        FeatureStdDevs = _featureStdDevs, ImputationMeans = _imputationMeans,
        TrainingGeneration = _trainingGeneration, Version = CurrentWeightsVersion
    }).ExportParameters();

    private double[][] FirstMoments() => [_mWIH!, _mWH1H2!, _mWH2H3!, _mWH3H4!, _mWH4O!, _mBH1!, _mBH2!, _mBH3!, _mBH4!];

    private double[][] SecondMoments() => [_vWIH!, _vWH1H2!, _vWH2H3!, _vWH3H4!, _vWH4O!, _vBH1!, _vBH2!, _vBH3!, _vBH4!];

    private NeuralTrainingState CaptureOptimizerState(NeuralTrainingState? destination = null)
    {
        var first = FirstMoments();
        var second = SecondMoments();
        destination ??= new NeuralTrainingState
        {
            FirstMoments = first.Select(values => new double[values.Length]).ToArray(),
            SecondMoments = second.Select(values => new double[values.Length]).ToArray(),
            InputWeightLimits = new double[_inputSize], InputBiasLimits = new double[Hidden1Size]
        };
        for (var index = 0; index < first.Length; index++)
        {
            Array.Copy(first[index], destination.FirstMoments[index], first[index].Length);
            Array.Copy(second[index], destination.SecondMoments[index], second[index].Length);
        }

        destination.Timestep = _adamTimestep;
        destination.OutputFirstMoment = _mBO;
        destination.OutputSecondMoment = _vBO;
        Array.Copy(_inputWeightLimits, destination.InputWeightLimits, _inputSize);
        Array.Copy(_inputBiasLimits, destination.InputBiasLimits, Hidden1Size);
        return destination;
    }

    private void RestoreOptimizerState(NeuralTrainingState state)
    {
        EnsureAdamState(_inputSize);
        var first = FirstMoments();
        var second = SecondMoments();
        for (var index = 0; index < first.Length; index++)
        {
            Array.Copy(state.FirstMoments[index], first[index], first[index].Length);
            Array.Copy(state.SecondMoments[index], second[index], second[index].Length);
        }

        _adamTimestep = state.Timestep;
        _mBO = state.OutputFirstMoment;
        _vBO = state.OutputSecondMoment;
        _inputWeightLimits = (double[])state.InputWeightLimits.Clone();
        _inputBiasLimits = (double[])state.InputBiasLimits.Clone();
    }

    private void RestoreTrainingState(NeuralTrainingState? state)
    {
        if (state is not null && state.Valid(_inputSize) && StateContainsParameters(state))
        {
            RestoreOptimizerState(state);
            return;
        }

        if (state is not null)
        {
            _logger?.LogWarning("Neural optimizer checkpoint is invalid; retaining valid prediction parameters without restoring the optimizer");
        }

        _mWIH = null;
        _adamTimestep = 0;
        _inputWeightLimits = Enumerable.Range(0, _inputSize)
            .Select(feature => Math.Max(WeightClamp, Enumerable.Range(0, Hidden1Size).Max(neuron => Math.Abs(_weightsIH[(neuron * _inputSize) + feature])))).ToArray();
        _inputBiasLimits = _biasH1.Select(value => Math.Max(WeightClamp, Math.Abs(value))).ToArray();
    }

    private bool StateContainsParameters(NeuralTrainingState state)
        => Enumerable.Range(0, Hidden1Size).All(neuron => Math.Abs(_biasH1[neuron]) <= state.InputBiasLimits[neuron]
            && Enumerable.Range(0, _inputSize).All(feature => Math.Abs(_weightsIH[(neuron * _inputSize) + feature]) <= state.InputWeightLimits[feature]));

    private void RebaseInputLimits(double[]? means, double[]? deviations)
    {
        (_inputWeightLimits, _inputBiasLimits) = TrainingMath.RebaseLimits((_inputWeightLimits, _inputBiasLimits), _featureMeans, _featureStdDevs, means, deviations);
    }
}
