using System.Collections;
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using UnityEngine;

//GraspAgent로 학습한 Grasp03모델의 집기 실력을 이어받아서 
//벨트 위 부품이미지만 보고 손목 각도를 고르는 에이전트
//매번 모델 각도 vs 정답 각도 결과를 기록

//에피소드 1회 (한판)
//부품 종류 무작위 -> 벨트 위 생성, 맞는 베이스를 배치
//안착하면 VisionCamera를 바로 부품 위로 옮기고 판단 ㅏ요청
//모델이 고른 각도로 부품을 잡기(학습 모드 PickTest/ 평가 모드 Assemble)
//결과 기록 -> 다음 에피소드

public class AssemblyGraspAgent : Agent
{
    #region 1. 에피소드 시작 (부품 생성, 카메라 이동, 판단 요청)

    [Header("연결")]
    public AssemblySequence assembly;      // niryo_one의 AssemblySequence (실제로 잡고 끼우는 일을 맡김)
    public ArmIK ik;                       // niryo_one의 ArmIK

    [Header("비전 카메라")]
    [Tooltip("VisionCamera. 부품이 생성되면 그 바로 위로 옮겨서 부품이 항상 이미지 가운데에 오게 함")]
    public Transform visionCamera;
    public bool centerCameraOnPart = true;
    [Tooltip("첫 판단 전에 VisionCamera의 Camera 컴포넌트를 한 번 껐다 켬.\n" +
             "Main 씬에서 시작 직후 CameraSensor NullReferenceException이 나는 문제 대응 (손으로 껐다 켜면 해결되던 것과 같음)")]
    public bool refreshCameraOnStart = false;

    [Header("평가")]
    [Tooltip("평가 횟수 (0 = 계속)")]
    public int evalEpisodes = 50;
    [Tooltip("켜면 Play 하자마자 평가 시작. 끄면 화면의 '평가 시작' 버튼으로 시작")]
    public bool runOnStart = false;
    public float settleTime = 0.5f;        // 생성 후 안착 대기 (초)

    [Header("학습 모드")]
    [Tooltip("켜면 학습 모드: 잡고 들어 올리기만 하고 각도 보상을 줌. Play 하자마자 시작")]
    public bool trainingMode = false;
    [Tooltip("학습 모드에서 Console에 몇 회마다 요약을 남길지")]
    public int trainingLogEvery = 50;

    bool running;                          // 진행 중인지 (평가 모드: '평가 시작' 버튼으로 켜짐 / 학습 모드: 처음부터 켜짐)
    bool waitingForDecision;               // 판단을 기다리는 중인지 (판단이 두 번 들어오는 것 방지)
    bool pendingEnd;                       // 에피소드를 끝내야 하는지 (실제 종료는 FixedUpdate에서. 2번 region 참고)
    PartInfo currentPart;                  // 이번에 생성한 부품
    bool cameraRefreshed;                  // 카메라 껐다 켜기를 이미 했는지 (처음 한 번만)

    int[] typeTryCount;                    // 종류별 시도 수
    int[] typeOptimalCount;                // 종류별 '최적' 수
    int[] typeSafeCount;                   // 종류별 '안전' 수
    int[] typeSeatCount;                   // 종류별 끼움 성공 수

    public override void Initialize()
    {
        if (assembly == null)
        {
            Debug.LogError("[AssemblyGraspAgent] Assembly가 비어 있습니다.");
        }
        if (ik == null && assembly != null)
        {
            ik = assembly.GetComponent<ArmIK>();
        }

        //partType에 들어있는 종류 개수 = cube, prism => 2개
        int typeCount = System.Enum.GetValues(typeof(PartType)).Length;
        typeTryCount = new int[typeCount];
        typeOptimalCount = new int[typeCount];
        typeSafeCount = new int[typeCount];
        typeSeatCount = new int[typeCount];

        //학습모드면 바로 시작, 평가 모드는 runOnStart가 꺼저있으면 버튼을 누를때까지 기다림
        running = runOnStart || trainingMode;
    }

    public override void OnEpisodeBegin()
    {
        StopAllCoroutines();
        StartCoroutine(BeginEpisode());
    }

    IEnumerator BeginEpisode()
    {
        waitingForDecision = false;
        pendingEnd = false;

        yield return new WaitUntil(() => running);
        yield return new WaitUntil(() => ik.Ready && !ik.IsMoving && !assembly.IsBusy);

        int partIndex = Random.Range(0, assembly.partPrefabs.Length);
        assembly.SpawnPart(partIndex);
        if (!trainingMode)
        {
            int baseIndex = assembly.FindBaseIndex(assembly.partPrefabs[partIndex].partType);
            if (baseIndex >= 0)
            {
                assembly.SpawnBase(baseIndex);
            }
        }
        currentPart = assembly.CurrentPart;

        yield return new WaitForSeconds(settleTime);

        if (refreshCameraOnStart && !cameraRefreshed && visionCamera != null)
        {
            Camera visionCameraComponent = visionCamera.GetComponent<Camera>();
            if (visionCameraComponent != null)
            {
                visionCameraComponent.enabled = false;
                yield return null;
                visionCameraComponent.enabled = true;
                yield return null;
                yield return null;
            }
            cameraRefreshed = true;
        }

        if (centerCameraOnPart && visionCamera != null && currentPart != null)
        {
            Vector3 cameraPosition = visionCamera.position;
            Vector3 partPosition = currentPart.transform.position;
            visionCamera.position = new Vector3(partPosition.x, cameraPosition.y, partPosition.z);
        }

        yield return null;
        yield return null;

        waitingForDecision = true;
        RequestDecision();
    }

    #endregion

    #region 2. 행동과 보상 (고른 각도로 잡고 점수 주기)

    [Header("행동")]
    public float yawStep = 15f;            // 행동 번호 x 15도 = 손목 각도 (Branch 0 Size 12 -> 0~165도)
    [Tooltip("모델 각도와 부품 방향 차이가 이 값 이하면 '최적' (도). 15도 간격이면 7.5 = 가장 가까운 칸")]
    public float matchTolerance = 7.5f;
    [Tooltip("이 값 이하면 '안전' (도). 바로 옆 칸까지 = 15. 잡기 판정 한계(19.8도) 안쪽")]
    public float safeTolerance = 15f;

    [Header("보상")]
    public float successReward = 1f;       // 평가 모드: 끼움 성공 / 학습 모드: 차이 0도일 때 최대 보상
    [Tooltip("학습 모드: 잡기 성공이지만 차이가 Safe Tolerance 이상일 때 보상")]
    public float minSuccessReward = 0.5f;
    public float failReward = -0.1f;       // 잡기 실패 (평가 모드: 끼움 실패 포함)

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!waitingForDecision || currentPart == null)
        {
            return;
        }
        waitingForDecision = false;

        float wristYaw = actions.DiscreteActions[0] * yawStep;
        StartCoroutine(Act(wristYaw));
    }

    //TODO
    //고른 각도를 실제로 잡아보고, 결과로 점수를 준뒤 에피소드 종료를 예약
    //학습모드 잡고 들어올려면 ->
    IEnumerator Act(float modelYaw)
    {
        //채점 준비
        //모델이 고른 각도가 정답과 얼마나 가까운지
        PartInfo part = currentPart;
        float partYaw = part.transform.eulerAngles.y;  //부품의 실제 방향(정답 계산용)
        float symmetry = part.symmetryDeg;
        float angleError = YawDiff(modelYaw, partYaw, symmetry);   //각도의 차이 
        float bestAngleError = YawDiff(assembly.HeuristicGraspYaw(part), partYaw, symmetry); //12개 중 가장 좋은 각도의 차이 
        float answerYaw = NearestBestYaw(modelYaw, partYaw, symmetry, bestAngleError); //로그에 보여줄 정답 각도

        bool match = angleError <= matchTolerance + 0.01f;//최적의 각도 0.01f은 소수 오차
        bool safe = angleError <= safeTolerance + 0.01f;//안전 각도

        //학습 모드(잡고 들어올리기까지만)\
        if (trainingMode)
        {
            yield return assembly.PickTest(modelYaw);
            bool graspOk = assembly.LastGraspOK;
            Record(part.partType, match, safe, graspOk, false, angleError); //결과 기록
            SetReward(graspOk ? AngleReward(angleError) : failReward); //성공이면 각도 점수 (0.5~1) 실패면 -0.1점

            //Tensorboard 그래프
            //1과 0을 보내면 Tensorboard가 평균을 내서 비율로 그려줌
            StatsRecorder statsRecorder = Academy.Instance.StatsRecorder;
            statsRecorder.Add("Grasp/AngleError", angleError);
            statsRecorder.Add("Grasp/Optimal", match ? 1f : 0f);
            statsRecorder.Add("Grasp/Safe", safe ? 1f : 0f);
            statsRecorder.Add("Grasp/Success", graspOk ? 1f : 0f);

            //trainingLogEvery회마다 콘솔에 최근 성적을 요약
            if (trainingLogEvery > 0 && episodeCount % trainingLogEvery == 0)
            {
                Debug.Log($"[GraspTrain] {RecentSummary()} 누적 {episodeCount}회");
            }

            pendingEnd = true;
            yield break;
        }
        // 평가 모드
        yield return assembly.Assemble(modelYaw);
        bool grasped = assembly.LastGraspOK;
        bool seated = assembly.LastSeated;
        Record(part.partType, match,safe,grasped,seated,angleError);
        SetReward(seated ? successReward : failReward);

        Debug.Log($"[GraspEval] {episodeCount}회 {part.partType} 부품 {partYaw:F0}도 | " +
            $"모델 {modelYaw:F0}도 차이{angleError:F0}도 " +
            $"{(grasped ? "잡기성공" : "잡기 실패")} | {(seated ? "끼움 성공" : "끼움실패")}"
        );

        if(evalEpisodes > 0 && episodeCount >= evalEpisodes)
        {
            running = false;
            Debug.Log($"[GraspEval 평가끝 {Summary()}");
        }

        pendingEnd = true;
    }

    void FixedUpdate()
    {
        if (!pendingEnd)
        {
            return;
        }
        pendingEnd = false;
        EndEpisode();
    }

    static float YawDiff(float yaw, float partYaw, float symmetry)
    {
        if (symmetry <= 0f)
        {
            return 0f;
        }
        return Mathf.Abs(Mathf.Repeat(yaw - partYaw + symmetry * 0.5f, symmetry) - symmetry * 0.5f);
    }


    // TODO 구현하기
    // 학습 모드 보상
    //차이 0도 성공보상 1점, 0.5점, 차이7.5도 ->0.75
    float AngleReward(float angleError)
    {
        //보상을 줄 때 차이값이 7.5도면 ->0.75점수. 차이가 0도이면 1점. 15도일때는 0.5

        //15를 0~1로 자른 값
        //t = 차이
        //Lerp(1.0, 0.5, t)

        float t = Mathf.Clamp01(angleError / Mathf.Max(1f, safeTolerance));
        return Mathf.Lerp(successReward, minSuccessReward, t);


       // return 0f;
    }

    //로그에 보여줄 
    float NearestBestYaw(float modelYaw, float partYaw, float symmetry, float bestAngleError)
    {
        int actionCount = Mathf.RoundToInt(180f / yawStep);
        float bestYaw = 0f;
        float bestDistance = float.MaxValue;
        for (int action = 0; action < actionCount; action++)
        {
            float yaw = action * yawStep;
            //모델 좋은 각도 아니면 건너뜀
            if (YawDiff(yaw, partYaw, symmetry) > bestAngleError + 0.01f)
            {
                continue;
            }
            float distance = Mathf.Abs(Mathf.Repeat(yaw - modelYaw + 90f, 180f) - 90f);

            //최적의 값을 찾으면 업데이트
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestYaw = yaw;
            }
        }
        return bestYaw;
    }

    #endregion

    #region 3. 결과 기록 (통계) - 핵심 흐름이 돌아간 뒤에 추가

    int episodeCount;                      // 전체 시도 수
    int optimalCount;                      // '최적' 각도를 고른 수
    int safeCount;                         // '안전' 각도를 고른 수
    int graspCount;                        // 잡기 성공 수
    int seatCount;                         // 끼움 성공 수 (평가 모드만)
    readonly List<float> angleErrors = new List<float>();   // 매번의 각도 차이 (평균 차이 계산용)

    const int RecentWindow = 100;
    readonly Queue<Vector3> recentResults = new Queue<Vector3>();   // 한 번의 결과를 Vector3 하나에: x = 최적, y = 안전, z = 잡기 (각 1 또는 0)
    Vector3 recentResultSum;               // 최근 100회 결과의 합 (x, y, z 각각 = 최적 수, 안전 수, 잡기 수)

    void Record(PartType type, bool match, bool safe, bool grasped, bool seated, float angleError)
    {
        episodeCount++;
        if (match)
        {
            optimalCount++;
        }
        if (safe)
        {
            safeCount++;
        }
        if (grasped)
        {
            graspCount++;
        }
        if (seated)
        {
            seatCount++;
        }
        angleErrors.Add(angleError);

        //최근100가지 관리 //세가지 (최적, 안전,잡기)를 Vector3에 담아서 한번에 넣고 뻄
        Vector3 recentResult = new Vector3(match ? 1f : 0f, safe ? 1f : 0f, grasped ? 1f : 0f);
        recentResults.Enqueue(recentResult);
        recentResultSum += recentResult;
        if (recentResults.Count > RecentWindow)
        {
            recentResultSum -= recentResults.Dequeue();
        }

        int typeIndex = (int)type;
        typeTryCount[typeIndex]++;
        if (match)
        {
            typeOptimalCount[typeIndex]++;
        }
        if (safe)
        {
            typeSafeCount[typeIndex]++;
        }
        if (seated)
        {
            typeSeatCount[typeIndex]++;
        }
    }

    static float Pct(int count, int total) => total > 0 ? 100f * count / total : 0f;

    string Summary()
    {
        float averageError = 0f;
        foreach (float error in angleErrors)
        {
            averageError += error;
        }
        if (angleErrors.Count > 0)
        {
            averageError /= angleErrors.Count;
        }
        return $"{episodeCount}회 | 각도 최적 {Pct(optimalCount, episodeCount):F0}%, 안전(<={safeTolerance:F0}도) {Pct(safeCount, episodeCount):F0}%, 평균 차이 {averageError:F1}도 | " +
               $"잡기 {graspCount} ({Pct(graspCount, episodeCount):F0}%) | 끼움 {seatCount} ({Pct(seatCount, episodeCount):F0}%)";
    }

    string RecentSummary()
    {
        int recentCount = recentResults.Count;
        if (recentCount == 0)
        {
            return "최근 0회";
        }
        return $"최근 {recentCount}회 | 최적 {100f * recentResultSum.x / recentCount:F0}%, 안전 {100f * recentResultSum.y / recentCount:F0}%, 잡기 {100f * recentResultSum.z / recentCount:F0}%";
    }

    void ResetStats()
    {
        episodeCount = 0;
        optimalCount = 0;
        safeCount = 0;
        graspCount = 0;
        seatCount = 0;
        System.Array.Clear(typeTryCount, 0, typeTryCount.Length);
        System.Array.Clear(typeOptimalCount, 0, typeOptimalCount.Length);
        System.Array.Clear(typeSafeCount, 0, typeSafeCount.Length);
        System.Array.Clear(typeSeatCount, 0, typeSeatCount.Length);
        angleErrors.Clear();
        recentResults.Clear();
        recentResultSum = Vector3.zero;
    }

    #endregion

    #region 4. 테스트 (새 변수 없음)

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        ActionSegment<int> discreteActions = actionsOut.DiscreteActions;
        discreteActions[0] = 0;
        if (currentPart == null)
        {
            return;
        }
        discreteActions[0] = Mathf.RoundToInt(assembly.HeuristicGraspYaw(currentPart) / yawStep);
    }

    #endregion

    #region 5. 화면 표시 (새 변수 없음. 3번 region의 통계를 표시)

    void OnGUI()
    {
        if (typeTryCount == null)
        {
            return;
        }

        System.Text.StringBuilder text = new System.Text.StringBuilder();
        text.Append(trainingMode ? "[벨트 환경 학습]\n" : $"[모델 평가] {(running ? "진행 중" : "대기")}\n");
        text.Append(Summary());
        int lineCount = 2;
        if (trainingMode)
        {
            text.Append("\n" + RecentSummary());
            lineCount++;
        }
        foreach (PartType partType in System.Enum.GetValues(typeof(PartType)))
        {
            int typeIndex = (int)partType;
            text.Append($"\n{partType}: 최적 {typeOptimalCount[typeIndex]} , 안전 {typeSafeCount[typeIndex]} , 끼움 {typeSeatCount[typeIndex]} / {typeTryCount[typeIndex]}회");
            lineCount++;
        }

        float boxWidth = 520f;
        float boxHeight = 12 + lineCount * 18;
        Rect box = new Rect(Screen.width - boxWidth - 10, Screen.height - boxHeight - 40, boxWidth, boxHeight);
        GUI.Box(box, text.ToString());

        if (trainingMode)
        {
            return;
        }

        GUI.enabled = !running;
        if (GUI.Button(new Rect(box.x, box.yMax + 4, 150, 26), evalEpisodes > 0 ? $"평가 시작 ({evalEpisodes}회)" : "평가 시작"))
        {
            ResetStats();
            running = true;
        }

        GUI.enabled = running;
        if (GUI.Button(new Rect(box.x + 156, box.yMax + 4, 100, 26), "멈춤"))
        {
            running = false;
        }
        GUI.enabled = true;
    }

    #endregion
}

