using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using UnityEngine;


//GraspAgent는 이미지만 보고 손목각도를 고른다.
//에피소드 1회 = 부품 1개 
//로봇 대기 위치로 -> 부품을 랜덤위치, 랜덤회전으로 생성 
//판단요청 -> 손목 각도를 선택
//그 각도로 내려가서 잡고 들어올림 
//잡혀있으면 +1, 놓치면 failReward -> 다음 에피소드

public class GraspAgent : Agent
{
    #region 1. 에피소드 시작(부품 생성, 카메라 이동, 판단 요청
    [Header("연결")]
    public PickPlace sequence; //실제로 잡는 일을 맡김
    public ArmIK ik;
    public GripperController gripper;

    [Header("부품 프리팹 (순서와 대칭각도를 맞추세요)")]
    public Rigidbody[] partPrefabs;              //프리팹 cubePart, PrismPart
    public float[] symmetryDeg = { 90f, 180f };  // 각 프리팹이 몇도마다 같은 모양인지. 큐브 90 삼각기둥 180

    [Header("비전 카메라")]
    public Transform visionCamera;
    public bool centerCameraOnPart = true;

    [Header("생성 범위(월드 좌표,m")]
    public Vector2 spawnRangeX = new Vector2(0.23f, 0.33f);  //x최소 y최대
    public Vector2 spawnRangeZ = new Vector2(-0.06f, 0.06f);
    public float settleTime = 0.5f;                         //생성후 안착 대기 

    //지금 에피소드 상태 
    private Rigidbody currentPart;    //이번 에피소드에서 생성한 부품 
    private int currentPrefabIndex;   //그 부품이 partPrefabs의 몇번째인지 
    private bool waitingForDecision;  // 판단을 기다리는 중인지 (판단이 두번 들어오는 것을 방지)

    //부품별 통계 (큐브, 프리즘)
    private int[] partTryCount;             //부품별 시도수
    private int[] partSuccessCount;         //부품별 성공 수 
    private int[] partRecentSuccessCount;   //부품별 최근 성공수 
    private Queue<int>[] partRecentResults; // 부품별 최근 결과
    private const int PartRecemtWindow = 50; // 부품별 최근 50회

    //Initialize는 Agent를 처음 만들때 한번만 호출(Awake랑 비슷)
    public override void Initialize()
    {
        if (sequence == null)
        {
            Debug.LogError("[GraspAgent] sequence가 비어있습니다");
        }
        if (partPrefabs == null || partPrefabs.Length == 0)
        {
            Debug.LogError("[GraspAgent] sequence가 비어있습니다");
        }
        if (symmetryDeg.Length != partPrefabs.Length)
        {
            Debug.LogWarning("[GraspAgent]symmetry Deg개수와 partPrefabs 개수를 맞추세요");
        }

        int partCount = partPrefabs.Length; //부품개수
        partTryCount = new int[partCount];
        partSuccessCount = new int[partCount];
        partRecentSuccessCount = new int[partCount];
        partRecentResults = new Queue<int>[partCount];   //Queue를 담을 을 만듬 

        //partTryCount = new int[2]; //[0,0]
        // partRecentResults = new Queue<int>[2];  // [null,null]

        //숫자 배열을 만들면 바로 0으로 채워지지만 Queue같은 객체 배열은 null로 채워짐 
        //그래서 칸마다 new로 하나씩 만들어 넣어줘야함

        for (int i = 0; i < partCount; i++)
        {
            //칸마다 진짜 Queue를 하나씩 만들어 넣기 
            partRecentResults[i] = new Queue<int>();
        }


    }
    //Agent가 에피소드를 시작할때 자동 호출 OnEpisodeBegin()
    public override void OnEpisodeBegin()
    {
        StopAllCoroutines();  //남아있는 코루틴을 멈추고 새 에피소드 시작 준비 
        StartCoroutine(BeginEpisode());
    }

    //로봇 이동, 안착 대기처럼 시간이 걸리는 일이있어서 코루틴으로 만듦 
    //이전 부품 정리 -> 로봇 대기 위치로 -> 새 부품 생성 -> 카메라를 부품 위로 -> 판단 요청 
    IEnumerator BeginEpisode()
    {
        waitingForDecision = false;

        //1. 이전 부품 정리 ??샤갈 내가 어캐알아
        if (gripper.HeldPart != null)
        {
            yield return gripper.Open();
        }

        if (currentPart != null)
        {
            currentPart.gameObject.SetActive(false); //카메라에 담지 않도록 숨김처리
            //Destroy는 프레임 끝에 지워짐. 
            Destroy(currentPart.gameObject);
            currentPart = null;
        }

        //2. 로봇이 한가할때까지 기다린뒤. 대기 위치로 보낸다
        //미션1 
        //ik가 ready될때까지
        //기다리기를 시전, 어떤 조건일떄 기다려야하는지 생각해보고 구현 
        // !IsBusy && ik != null && ik.Ready && !ik.IsMoving;
        yield return new WaitUntil(() => ik.Ready && !ik.IsMoving && !sequence.IsBusy && !gripper.IsBusy);

        //미션2 
        // ik 사용해서 대기위치로 이동 
        //yield return ik.MoveTo(homePositioin, 0f);
        yield return ik.MoveTo(sequence.homePositioin, 0f);

        // 부품 생성 : 랜덤 종류, 랜덤 위치, 랜덤 회전(0~180도)
        currentPrefabIndex = Random.Range(0, partPrefabs.Length);  //Range(0,n) ->0 ~n-1개 뽑음  사실 0,1뽑음 
        Rigidbody prefab = partPrefabs[currentPrefabIndex];
        Vector3 spawnPosition = new Vector3(
            Random.Range(spawnRangeX.x, spawnRangeX.y),
            prefab.transform.position.y,
            Random.Range(spawnRangeZ.x, spawnRangeZ.y)
            );

        //Quaternion
        Quaternion spawnRotation = Quaternion.Euler(0f, Random.Range(0, 180f), 0f);

        //부품을 위해서는 어떤것을 생성할지와 생성위치, 회전값
        //Instantiate
        //부품을 생성한 후 저장하세요

        //미션1 Instantiate함수를 사용하여 부품을 생성하세요 . 생성한 부품을 저장합니다 
        currentPart = Instantiate(prefab, spawnPosition, spawnRotation);

        //4. 떨어기저가 흔들리던 멈출때까지 대기
        yield return new WaitForSeconds(settleTime);

        //5. 카메라를 부품 바로 위로 이동 (x,z값을 부품과 같게 . 카메라의 높이 회전은 그래로)
        //부품이 매번 다른 위치에 생기므로, 카메라를 옮겨야 항상 사진 가운데에 부품이 놓인다.
        if (centerCameraOnPart && visionCamera != null && currentPart != null)
        {
            Vector3 cameraPosition = visionCamera.position;
            Vector3 partPosition = currentPart.position;

            visionCamera.position = new Vector3(partPosition.x, cameraPosition.y, partPosition.z);
        }

        //6. 판단 요청 
        waitingForDecision = true;
        RequestDecision();
        // RequestDecision(); 는 Agent클래스 안에있는 함수 "지금 판단해줘" 요청
        // Decision Requester(컴포넌트)는 정해진 간격마다 자동으로 요청
        // Decision Requester를 사용하지 않는 이유: 부품이 아직 떨어지는 중이거나 카메라가 옮겨지기전에 판단되면 안되서.
    }

    //관측은 카메라가 자동으로 넘겨줌 
    //관측 정보 CollectObservation()은 작성하지 않음 -> Camera Sensor가 자동으로 관측 정보를 넘겨주지 않기 때문

    #endregion

    # region 2. 행동과 보상(고른 각도로 잡고 점수 주시)
    [Header("행동")]
    public float yawStep = 15f;// 번호 * 15도 = 손목 각도 (0~165도 , 12개)

    [Header("보상")]
    public float successReward = 1f;
    public float failReward = -0.1f;

    [Header("모드")]
    public bool trainingMode = true; //켜면 학습모드 : 잡고 들어 올려서 성공여부만 확인(통으로 옮기지 않는다)
                                     //꺼져있으면 시연모드 : 잡은 부품을 통에 넣는다

    public int[] binForPrefab = { 0, 1 }; //큐브 -> 0번통, 삼각기둥 ->1번통
    public int maxPartsKeptInBins = 10; //시연모드에서 통에 남겨둘 부품 최대 개수
    private readonly Queue<GameObject> placedParts = new Queue<GameObject>();  //시연 모드에서 통에 넣은 부품들

    //신경망이 행동을 고르면 ml agent가 OnActionReceived를 호출
    //행동 번호(0~11)손목 각도로 바꿔서 실행한다
    public override void OnActionReceived(ActionBuffers actions)
    {
        //판단 요청한 상태가 아니라면 무시 (한 에피소드에 한번만 행동)
        if (!waitingForDecision || currentPart ==null) { return; }

        waitingForDecision=false;

        //DiscreteActions[0]은 0번 가지에서 고른 번호 (0~11)
        //손목 각도 = 신경망이 골라준 번호 *yawStep(15도)
        float wristYaw = actions.DiscreteActions[0] * yawStep;
        StartCoroutine(Act(wristYaw));
    }

    //고른 각도로 실제로 잡아보고, 결과를 보고 점수를 준뒤에 에피소드를 끝냄
    IEnumerator Act(float wristYaw)
    {
        // 시연 모드에서 넣을 통 번호(칸이 없으면 0번 통)
        int binIndex = currentPrefabIndex < binForPrefab.Length ? binForPrefab[currentPrefabIndex] : 0;

        //잡기 실행
        yield return sequence.Run(binIndex, wristYaw, trainingMode, currentPart);

        //결과 : 들어올린 뒤에도 잡혀있으면 성공
        bool success = sequence.LastSuccess;
        //TODO Record 함수를 만들고 호출하기. 
        Record(success);  //결과 기록

        //보상
        SetReward(success ? successReward : failReward);

        // 시연 모드에서 통에 넣은 부품은 남겨둬야한다(다음 에피소드에서도 삭제하지 않음)
        if (!trainingMode &&  success && currentPart != null)
        {
            placedParts.Enqueue(currentPart.gameObject);
            currentPart.tag = "Untagged";
            currentPart = null;            //null 처리해서 비워야 BeginEpisode에서 삭제되지 않음 

            if (placedParts.Count > maxPartsKeptInBins)
            {
                GameObject oldestPart = placedParts.Dequeue();

                if (oldestPart != null)
                {
                    Destroy(oldestPart);
                }
            }
        }

        EndEpisode(); //Agent가 OnEpisodeBegin 다시 호출 -> 다음 부품

        #endregion
    }

    #region 3. 결과 기록 (성공률 통계)
    // 통계 화면 표시용
    private int episodeCount; //전체 시도수
    private int successCount; //전체 성공 수 
    private Queue<int> recentResults = new Queue<int>(); //최근 100회 결과(성공1, 실패0)
    private int recentSuccesCount; // 최근 100회중 성공 수 

    //Record 함수는 부품별 성공률을 따로 기록해 그래프로 보낸다.
    //전체 성공률은 초반 실패까지 섞여있으니 최근100회(부품별 50회)만 따로 세서 현재의 실력을 본다
    private void Record(bool success)
    {
        episodeCount++;
        if (success)
        {
            successCount++;
        }
        //최근 100회 성공률을 Queue에 결과에 넣고 , 100개가 넘으면 가장 오래된 것은 뺀다 
        // Enqueue 맨 뒤에 값넣기, Dequeue = 맨앞(가장 오래된것)꺼내기

        //미션1 삼항 연산자를 사용해서 int result 변수에 성공값을 넣으세요
        int result = success ? 1 : 0;

        //미션2 result 변수를 queue에 넣기
        recentResults.Enqueue(result);

        //미션3 recentSuccesCount에 현재 result를 누적하기
        recentSuccesCount += result;

        //미션4 recentResults큐의 개수를 체크하고 100개가 넘는다면
        //recentSuccesCount에서 현재의 recentResults의 Dequeue를 빼세요
        if (recentResults.Count > 100)
        {
            recentSuccesCount -= recentResults.Dequeue();
        }

        // 부품별 기록
        //프리팹 번호(0번 cubePart, 1번 = prismPart)
        int index = currentPrefabIndex;

        //안전확인 배열이만들어졌는지, 칸 번호가 배열 범위 안일때만 기록
        if (partTryCount != null&& index >=0 && index < partTryCount.Length)
        {
            //이 부품의 전체 시도수, 성공수 (전체 성공률: 성공수 / 시도수)
            partTryCount[index]++;          // partTryCount[Index] = partTryCount[Index] +1
            if (success)
            {
                partSuccessCount[index]++;
            }
            //이 부품의 최근 50회 성공률 
            // 부품 전용 큐에 이번 결과 넣기
            //큐의 줄이 50개를 넘으면 맨 앞을 꺼내고 , 그 값만큼 성공수에서 뺸다 
            partRecentResults[index].Enqueue(result);
            partRecentSuccessCount[index]+= result;

            if (partRecentResults[index].Count > PartRecemtWindow)
            {
                partRecentSuccessCount[index] -= partRecentResults[index].Dequeue();
            }

            //TensorBoard에 이 결과 값을 보낸다
            //Add() 그래프 카테고리 Grasp / Success 부품 이름
            Academy.Instance.StatsRecorder.Add($"Grasp/Success {partPrefabs[index].name}", result);
        }
    }
    #endregion

    #region 4. 수동 테스트 
    //Heuristic only일때 신경망 대신 사람이 정답 각도를 골라주는 함수
    //학습 전에 전체 흐름 (생성 -> 판단 -> 잡기 -> 점수) 이 제대로 도는지 확인

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        //고른 행동을 적을 칸, 기본값은 0 (0도)
        ActionSegment<int> discretActions = actionsOut.DiscreteActions;
        discretActions[0] = 0;

        if (currentPart ==null)
        {
            return;
        }

        //이 부품은 몇도마다 같은 모양인지 (큐브 90, 삼각기둥 180)
        float symmetry = currentPrefabIndex< symmetryDeg.Length ? symmetryDeg[currentPrefabIndex] : 0;

        if (symmetry < 0f)
        {
            return;          // 대상 값이 없으면 0번 각도 그대로 
        }

        //손목 각도 알아내기

        //부품의 실제 회전값
        float partYaw = currentPart.transform.eulerAngles.y;

        //고를 수 있는 각도 개수
        int actionCount = Mathf.RoundToInt(180f / yawStep);

        //지금까지 가장 잘 맞았던 번호
        int bestAction = 0;

        //그때의 차이(처음엔 아주 큰 값)
        float bestError = float.MaxValue;

        //12개 손목각도를 하나씩 대보고, 부픔과 가장 잘 맞는 것을 고른다 
        for (int action = 0; action < actionCount; action++)
        {
            //손목과 부품이 실제로 몇도 어긋났다 (대칭 고려)

            //예 부품이 100도 손목이 15도 
            //그냥 빼면 -85 -> 틀렸다라고 판단
            //실제로는 100도 = 10도와 같은 모양이니 15-10 = 5도 차이 -> 거의 맞음
            //1. 손목 - 부품 : 그냥 뺀 차이(15-100 = -85)
            //2.  대칭/2 -> Repeat : 대칭 단위로 되감기(-85+ 45 = -40 ->Repeat(-40,90) = 50
            //3. -대칭/2 : 범위를 -45~45로 (50-45 = 5)
            //Mathf.Repeat(값, 길이) 값을 0~ 길이 사이로 되감는다
            //예 큐브 부품 50도, 손목이 45도 > -5도 // 손목이 60도 0> 10도 => 45도가 정답
            float difference = Mathf.Repeat(action * yawStep - partYaw + symmetry * 0.5f, symmetry) -symmetry*0.5f;

            //더 작은 차이를 구했다면, 이 각도를 정답 후보로 사용 
            //(Mathf.Abs 절댓값 함수
            if (Mathf.Abs(difference)< bestError)
            {
                bestError = Mathf.Abs(difference);
                bestAction = action;
            }
        }

        //가장 잘 맞는 행동 번호를 줌 
        discretActions[0] = bestAction;
    }
    #endregion

    #region 5. 화면 표시
    //학습 모드 시도 횟수/ 성공 횟수 (100%)
    //최근 100회 성공률(%)
    //cubePart
    //prisimPart

    private void OnGUI()
    {
        //전체 성공률
        float totalRate = episodeCount >0 ? 100f*successCount / episodeCount : 0f;

        //최근 100회 성공률 
        float recentRate = recentResults.Count > 0 ? 100f * recentSuccesCount / recentResults.Count : 0f;

        string mode = trainingMode ? "학습모드" : "시연 모드";

        //StringBuilder : 여러 줄 글자를 이어붙일때 쓰는 도구. Append를 사용하여 뒤에 붙여준다
        StringBuilder text = new StringBuilder();
        text.Append($"[{mode} 시도 {episodeCount}회 / 성공{successCount}회 ({totalRate:F1}%)]\n");
        //미션1 최근 {}회 성공률 {} %
        text.Append($"최근 {recentResults.Count}회 성공률{recentRate:F1}% ");

        int lineCount = 2;  //지금까지 쓴 줄수 (상자높이 계산)

        if (partTryCount != null)
        {
            for (int i = 0; i< partTryCount.Length; i++)
            {
                //부품 전체
                //성공수 / 시도 수 ( 전체 성공률 ) / 최근 50회 성공률
                float partTotalRate = partTryCount[i] > 0 ? 100f * partSuccessCount[i] / partTryCount[i] : 0f; //부품 전체
                float partRecentRate =
                    partRecentResults[i].Count > 0 ? 100f * partRecentSuccessCount[i] / partRecentResults[i].Count : 0f;//부품 최근

                text.Append($"\n{partPrefabs[i].name}: {partSuccessCount[i]} / {partTryCount[i]} " +
                            $"({partTotalRate:F1}%) / 최근 {partRecentResults[i].Count}회 {partRecentRate:F1}%");
                lineCount++;
            }
        }
        //상자 그리기
        // 높이 = 위아래 여백 12
        // 줄수 * 한줄 높이 18
        //부품이 늘어도 상자가 자동으로 커질수있도록 구현
        float boxHeight = 12 + lineCount * 18;

        //React(x,y,너비, 높이)
        //x = 화면 너비 - 390 -> 오른쪽 끝에서 390 안쪽
        //y = 화면 높이 상자 높이 10 = 아래 끝에서 10 위 
        GUI.Box(new Rect(Screen.width - 390, Screen.height - boxHeight-10, 380, boxHeight), text.ToString());
    }
    #endregion
}



