using System.Collections;
using UnityEngine;

//컨베이어 벨트위의 부품을 집어서,
//베이스의 구멍 방향에 맞게 손목을 돌린뒤,
//구멍에 끼우는 조립 동작을 하는 스크립트 
public class AssemblySequence : MonoBehaviour
{
    #region 1.준비와 대기 위치 
    public ArmIK iK;
    public GripperController gripper;
    public Transform partStopPoint; //부품 벤트 위 멈추는 자리
    public Transform basePoint;     // 베이스 벨트 위 조립 자리

    [Header("대기 위치")]
    public Vector3 homePosition = new Vector3(0.22f, 0.20f, 0f);
    [Tooltip("대기 위치로 돌아갈떄 관절 속도. 매번 같은 관절 자세로 돌아가 손목 꼬임을 풀어줌")]
    public float homeJointSpeed = 90f;

    public bool IsBusy { get; private set; }

    public string Status { get; private set; } = "대기";  //현재 단계 화면 표시용

    private BaseInfo currentBase;   //현재 조립 대상 베이스 

    private float[] homeJoints; // 처음 대기 위치의 관절 각도 6개 (매 사이클 여기로 복귀)

    //손목 관절 hand link 가져오기
    ArticulationBody Wrist => iK.joints[iK.joints.Length - 1]; //손목 관절
    float WristAngle => Wrist.jointPosition[0] * Mathf.Rad2Deg;  //현재 손목 관절 각도 (도)

    // 로그용 손목각도 정보
    string WristInfo() => $"{WristAngle:F0}도 (한계 {Wrist.xDrive.lowerLimit:F0}~{Wrist.xDrive.upperLimit:F0}도)";

    private void Awake()
    {
        if (iK == null)
        {
            iK = GetComponent<ArmIK>();
        }
    }

    //Void Start 처럼 IEnumerator Start() 자동함수 
    IEnumerator Start()
    {
        IsBusy = true;

        //씬에 놓인 베이스가 BasePoint 근처(2cm)에 있으면 그것을 조립 대상으로 사용
        if (basePoint != null)
        {
            foreach (BaseInfo sceneBase in FindObjectsByType<BaseInfo>())
            {
                if ((sceneBase.transform.position - basePoint.position).sqrMagnitude <0.0004f)
                {
                    currentBase = sceneBase;
                    break;
                }
            }
        }

        yield return new WaitUntil(() => iK.Ready);
        Status = "대기 위치로";
        yield return GoHome();

        //대기 위치의 관절 각도를 저장-> 매번 이 자세로 돌아올수있도록 
        homeJoints = new float[iK.joints.Length];

        //미션1 
        //반복문을 사용하여 homeJoint 배열에 ik joint의 xDrive target 값을 넣어주기 
        for (int jointIndex = 0; jointIndex < homeJoints.Length; jointIndex++)
        {
            homeJoints[jointIndex] = iK.joints[jointIndex].xDrive.target;
        }

        Debug.Log($"[AssemblySequence] 대기 자세 저장 | 손목 관절 {WristInfo()}");
        Status = "대기";
        IsBusy = false;
    }

    // GoHome이 필요한 이유
    // 손목 관절은 돌수있는 범위가 정해져있음 -148~148
    //조립이 끝나면 손목 관절이 범위 끝에 가있을수도있음
    //이상태로 MoveTo로 대기위치, 손목0도 -> ik는 지금 자세에서 가장 가까운 답을 찾음
    // 그리퍼는 위에서 봤을떄 180도 돌려도 같기에 0도와 180도를 같은 답으로 인식할 가능성이있음
    // 그래서 손이 대기 위치에 왔을떄 손목 관절 끝 각도에 남아있을수도있음
    // 다음 조립떄 손목을 한쪽으로 거의 못돌려서 실패가능성을 챙김
    //그 각도까지 한번에 튀지 않도록 부드럽게 이동, 실제로 도착됐는지 확인하는 부분
    IEnumerator GoHome()
    {
        //아직 저장 전이면 (start의 첫 이동중이면) 돌아갈 각도가 없으므로 IK로 이동
        if (homeJoints == null)
        {
            yield return iK.MoveTo(homePosition, 0f);
            yield break;
        }
        // 1단계 이동시간 정하기 
        //지금 각 관절의 목표를 출발 각도로 기록
        // 저장 각도까지 가장 많이 돌아야하는 관절이 몇도인지 구한다 
        ArticulationBody[] joints = iK.joints; //관절 6개
        float[] startAngles = new float[joints.Length];
        float maxTurn = 0f;//가장 많이 돌아야하는 관절 각도 

        for (int jointIndex = 0; jointIndex < joints.Length; jointIndex++)
        {
            startAngles[jointIndex] = joints[jointIndex].xDrive.target;


            //가장 많이 돌아야하는 각도 찾기
            maxTurn = Mathf.Max(maxTurn, Mathf.Abs(homeJoints[jointIndex] -  startAngles[jointIndex]));
        }

        //이동시간 구하기
        //이동시간 = 가장 많이 도는 관절의 각도 / 관절 속도
        //예 손목이 135도 돌아야하고 속도가 90도/초 => 1.5초
        //모든 관절이 이 시간동안 같이 출발해서 도착한다.
        float duration = Mathf.Max(0.5f, maxTurn / Mathf.Max(1f, homeJointSpeed));

        //2단계 출발 각도에서 저장각도까지 조금씩 옮기기
        //progress 0에서 출발해서 1에서 도착
        //Time.fixedDeltaTime으로 duration에 맞춰 조금씩 커짐
        //blend progress를 SmoothStep으로 바꾼값. 처음과 끝에는 천천히, 가운데 빠르게 움직임. 
        //Lerp(출발, 저장, blend) blend가 0이라면 출발 가곧, 1이면 저장각도 0.5면 그중간
        //ArticulationDrive 값을 넣어줌. ArticulationDrive는 struct라서 값을 적용할때는 꺼내서 고친 뒤 다시 넣어줘야함 

        float progress = 0f;

        while (progress < 1f)
        {
            progress += Time.fixedDeltaTime / duration;                      //물리 한칸만큼 진행률을 조금 올림 

            float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));

            for (int jointIndex = 0; jointIndex < joints.Length; jointIndex++)
            {
                ArticulationDrive drive = joints[jointIndex].xDrive;

                drive.target = Mathf.Lerp(
                    startAngles[jointIndex],
                    homeJoints[jointIndex],
                    blend
                );

                joints[jointIndex].xDrive = drive;
            }
            yield return new WaitForFixedUpdate(); // 물리 한칸 기다렸다가 다음 각도로 
        }

        //3단계 실제로 도착할떄까지 기다리지
        //target 각도 = 이동해야할 지점
        //jointPosition = 실제 각도 
        // 모든 관절의 실제 각도가 저장각도와 0.5도 안으로 들어오면 끝 
        //최대 1.5초까지만 기다림

        for (float waited = 0f; waited < 1.5; waited+= Time.fixedDeltaTime)
        {
            bool allArrived = true;

            for (int jointIndex = 0; jointIndex < joints.Length; jointIndex++)
            {
                //Mathf.Abs(실제 각도 - 저장 각도) > 0.5f
                if (Mathf.Abs(joints[jointIndex].jointPosition[0]* Mathf.Rad2Deg - homeJoints[jointIndex]) >0.5f)
                {
                    allArrived=false; // 하나라도 아직 도착 안했다면 더 기다림
                    break;
                }
            }

            if (allArrived)
            {
                break;
            }

            yield return new WaitForFixedUpdate();


        }
    }

    #endregion

    #region 2. 부품과 베이스 준비
    [Header("프리팹")]
    public PartInfo[] partPreFabs;     //PartInfo가 붙은 부품 프리팹
    public BaseInfo[] basePrefabs;     //BaseInfo 붙은 부품 프리팹

    [Header("베이스 배치 흔들기 { 컨베이어로 베이스가 들어올떄 약가의 어긋남 재현")]
    public bool randomzeBase = true;        // 베이스를 놓을때 위치,방향을 무작위로 
    public float baseOffsetRange = 0.005f;  //BasePoint에서 x,z 각각 +- 이 거리안에서 무작위
    public bool baseRightAngles = true;     //켜져있으면 베이스를 직각으로 돌린다 0, 90, 180, 270도

    [Header("부품 생성 범위 PartStopPoint 기준")]
    public float spawnRangeX = 0.01f;     // +- 1cm로 벨트가 멈추는 위치의 흔들림 
    public float spawnRangeZ = 0.03f;     // +-3cm 벨트 가장자리에 손가락이 닿지 않는 범위 
    public bool randonRotation = true;    // 끄면 회전 각도 0
    public float settleTime = 0.5f;       // 생성 후 안착 대기 (초)

    private Rigidbody currentPart;        //현재 부품
    private PartInfo currentPartInfo;     //현재 부품의 정보

    public PartInfo CurrentPart => currentPartInfo;
    public BaseInfo CurrentBase => currentBase;

    //벨트 위 조립된 부품 정리 (잡고있는 부품 그대로)
    public void ClearPart()
    {
        if (currentPart != null)
        {
            //미션1
            //그리퍼가 잡고있는 부품이 현재부품과 같다면 return
            if (gripper.HeldPart == currentPart) return;

            //미션2 
            //현재 부품을 카메라에 남지않게 즉시 숨기
            currentPart.gameObject.SetActive(false);

            //미션3 
            //현재 부품을 삭제
            Destroy(currentPart.gameObject);
        }
        currentPart = null;
        currentPartInfo = null;
    }

    //부품 생성 : PartStopPart 주변 무작위 위치, 무작위 회전 
    public void SpawnPart(int index)
    {
        //부품 생성 못하는 조건이라면 return
        if (partPreFabs == null || index <0 || index >= partPreFabs.Length) return;

        ClearPart();

        //생성하기
        PartInfo prefab = partPreFabs[index];
        Vector3 stopPosition = partStopPoint.position;

        Vector3 spawnPosition = new Vector3(stopPosition.x + Random.Range(-spawnRangeX, spawnRangeX),
                                            stopPosition.y + prefab.halfHeight + 0.002f,    //벨트 윗면 + 부품 절반 + 2mm위에서 떨어뜨림 
                                            stopPosition.z + Random.Range(-spawnRangeZ, spawnRangeZ));

        float spawnYaw = randonRotation ? Random.Range(0f, 180f) : 0f;

        //미션
        //생성한 값을 spawndPart변수로 만들기
        //생성 Instantiate 함수 사용. 어떤 프리팹을 생성할지, 위치, 회전값       
        //currentPart = Instantiate(prefab, spawnPosition, spawnRotation);
        PartInfo spawndPart = Instantiate(prefab, spawnPosition, Quaternion.Euler(0, spawnYaw, 0));
        currentPartInfo = spawndPart;
        currentPart = spawndPart.GetComponent<Rigidbody>();
        Status = $"{prefab.name} 생성 (회전 {spawnYaw:F0}°)";
    }

    //베이스는 basePoint에 지정한 베이스를 놓음. randomizeBase라면 위치와 방향을 조금 흔듦
    public void SpawnBase(int index)
    {
        //1. 베이스 생성이 가능한지
        if (basePrefabs == null || index <0 || index >= basePrefabs.Length)
        {
            return;
        }
        //2. 현재 베이스가 존재한다면, 카메라에서 숨겨주고, 현재 베이스는 삭제
        if (currentBase != null)
        {
            currentBase.gameObject.SetActive(false);
            Destroy(currentBase.gameObject);
        }
        //3. randomizeBase가 true라면 새로운 포지션 x,y,z,값을 구합니다
        //y값은 90도 기준으로함 베이스는 0,90,180,270 -> Random.Range(0,4)
        Vector3 spawnPosition = basePoint.position;
        float spawnYaw = 0f;

        if (randomzeBase)
        {
            spawnPosition.x += Random.Range(-baseOffsetRange, baseOffsetRange);

            spawnPosition.z += Random.Range(-baseOffsetRange, baseOffsetRange);

            if (baseRightAngles)
            {
                spawnYaw = 90f * Random.Range(0, 4);
            }
        }


        //4. 현재 베이스를 생성 
        //Status 값을 업데이트
        currentBase = Instantiate(basePrefabs[index], spawnPosition, Quaternion.Euler(0f, spawnYaw, 0f));

        Status = $"{basePrefabs[index].name}배치 (회전 {spawnYaw:F0})";
    }

    //부품에 맞는 베이스를 고를 때 사용 
    public int FindBaseIndex(PartType type)
    {
        for (int i = 0; i < basePrefabs.Length; i++)
        {
            if (basePrefabs[i] != null &&  basePrefabs[i].holeType == type)
            {
                return i;
            }
        }
        return -1; // ??????????? 값이 없거나 초기화전이면 이런데는디 
    }
    #endregion

    #region 3. 잡을 손목 각도
    [Header("손목 각도")]
    public float yawStep = 15f;
    public int yawCount = 12;    //0도, 15, 165도

    //부품 방향에 맞는 손목 각도 고르기 
    //후보 12개를 하나씩 부품 방향과 비교하여 차이가 가장 적은 것 고르기 

    public float HeuristicGraspYaw(PartInfo part)
    {
        float symmetry = part.symmetryDeg;
        float partYaw = part.transform.eulerAngles.y; //위에서 본 부품 방향
        float bestYaw = 0f;                           // 지금까지 찾은 가장 좋은 손목 각도 
        float bestError = float.MaxValue;

        for (int action = 0; action < yawCount; action++)
        {
            float yaw = action * yawStep;  //후보각도 0,15~ 165

            //후보각도와 부품 방향의 차이 구하기 
            //그냥 빼면 안됨 대칭각도 계산해야함
            float error = Mathf.Abs(Mathf.Repeat(yaw - partYaw + symmetry * 0.5F, symmetry) - symmetry*0.5f);

            if (error < bestError)
            {
                bestError = error;
                bestYaw = yaw;
            }
        }
        return bestYaw;
    }
    #endregion

    #region 4. 조립 계산
    //GripPoint : 로봇 손의 기준점
    //FlatDelta : 위에서 본 두점 사이의 거리 ( 높이는 무시)
    //PartHoleYawErr : 부품을 구멍 방향에 맞추려면 손목 몇도더 돌려야하나 
    //CheakSeated : 다 놓고 난 뒤 제대로 끼워졌나 확인 

    [Header("끼움 판정")]
    //높이: 부품이 구멍 바닥까지 내려갔는지 3mm 넘게 떠있으면 실패 (구멍에 덜 들어가고 위에 얹힌경우) 
    public float seatHeightTolerance = 0.003f;

    //위치: 부품 중심이 구멍 중심에 있는지. 4mm 넘게 옆으로 비껴있으면 실패
    public float seatCenterTolerance = 0.004f;

    //기울기: 부품이 똑바로 서있는지 5도 넘게 기울이면 실패 ( 구멍 모서리에 걸려서 비스듬한 경우)
    public float seatTiltTolerance = 5f;

    Transform GripPoint => gripper.attachPoint != null ? gripper.attachPoint : iK.tip;  //로봇 손 기준점

    //베이스 위에서 손이 내려갈때, 얼마나 옆으로 치우쳤는지를 잰다
    //예를 들어서 오른쪽으로 3mm 치우쳤다면 -> 왼쪽으로 3mm 옮긴다
    //수평면에서 a-b(높이 차이는 버림)
    static Vector3 FlatDelta(Vector3 a, Vector3 b)
    {
        float differenceX = a.x - b.x;//좌우 차이
        float differenceZ = a.z - b.z;//앞뒤 차이

        return new Vector3(differenceX, 0, differenceZ);
    }

    //부품 방향과 베이스 구멍 방향 사이 (대칭 고려)
    //예 큐브(대칭90) -> 100도, 구멍 0->+10도
    static float PartHoleYawErr(PartInfo part, BaseInfo baseInfo)
    {
        float symmetry = part.symmetryDeg;    // 이만큼 돌리면 같은 모양 큐브 90 삼각기둥 180
        float halfSymmetry = symmetry * 0.5f; //대칭의 절반 : 큐브 45, 삼각기둥 90
        float partYaw = part.transform.eulerAngles.y; //부품 방향(위에서 본 각도)
        float holeYaw = baseInfo.HoleYaw;

        //1. 그냥 차이값 구하기 
        float rawDifference = partYaw - holeYaw;
        //2. 대칭 단위로 접어서- 절반 ~+절반 범위로 
        //접는다 = 큰 각도를 같은 모양인 작은 각도로 변한다
        return Mathf.Repeat(rawDifference + halfSymmetry, symmetry) - halfSymmetry; // Mathf.Repeat(a,b) a가 b미만으로 반복해서 값나옴

    }

    //끼움 판정 : 높이, 중심, 기울기 세가지 기준이 충족할때 성공
    //판정 결과를 전달 (성공 여부 bool, 판정에 결과 문장 string)
    private bool CheakSeated(PartInfo part, BaseInfo baseInfo, out string info)
    {
        Transform partTransform = part.transform;

        //1. 높이 : 부품 바닥이 베이스의 구멍 바닥에 닿았는지 
        float bottom = partTransform.position.y - part.halfHeight; //부품 바닥 높이
        //베이스 구멍 바닥과의 차이 구하기
        float heightError = bottom - baseInfo.FloorY;

        //2. 중심
        //옆에서 봤을때 부품 중심과 베이스 구멍중심이 얼마나 떨어져있는지 
        Vector2 centerOffset = new Vector2(partTransform.position.x - baseInfo.HoleCenter.x,
            partTransform.position.z - baseInfo.HoleCenter.z);

        //3. 기울기
        //부품의 위쪽 방향이 하늘과 몇도 벌어졌는지 똑바로 서있다 0도
        float tilt = Vector3.Angle(partTransform.up, Vector3.up);

        //4. 위에 네가지를 모두 통과하면 성공
        bool okHeight = Mathf.Abs(heightError) <= seatHeightTolerance;

        //magnitude 두점 사이의 직선 거시
        //좌우 3mm, 앞뒤 4mm 떨어져있으면, 실제 거리는 5mm 
        bool okCenter = centerOffset.magnitude <= seatCenterTolerance;

        bool okTilt = tilt <= seatHeightTolerance;

        bool ok = okHeight && okTilt && okCenter;

        //1000을 곱해서 m -> mm, F1 소수 첫째자리까지 표시 
        info = $"{(ok ? "끼움 성공" : "끼움 실패")} | 높이 {heightError * 1000f:F1}mm, 중심 {centerOffset.magnitude * 1000f:F1}mm"
                + $", 기울기 {tilt:F1}도";

        //부품과 베이스 종류가 안맞으면 표시하기
        if (!baseInfo.Accepts(part))
        {
            info += "(틀린 조합)";
        }
        return ok;
        #endregion
    }

    #region 5.손목 돌리기(관절 한계 관리)
    //부품을 잡은 채 베이스 위로 가면, 부품 방향과 베이스방향이 다를수있다
    //손목을 돌려서 맞춰야하는데, 손목 관절은 돌수있는 범위가 정해져있음
    //큐브는 90도 마다 같은 모양임. +30도 돌리기, -60도 돌리기가 같은 결과.
    //관절 범위 안이면서 가장 적게 도는 쪽을 고르기 

    public float wristLimitMargin = 10f;        //손목을 끝까지 돌리지 않고 10도 여유를 남김
    public float wristStepDeg = 60;             //손목을 크게 돌릴때 한번에 돌릴수있는 최대 각도(90도 미만이여야 원하는 방향으로 돌림)
    
    //손목에 각도 제한이 있는지 
    //true면 범위 한계가 정해져있다는 뜻 
    bool wristLimit => Wrist.xDrive.lowerLimit < Wrist.xDrive.upperLimit;

    #endregion
}
