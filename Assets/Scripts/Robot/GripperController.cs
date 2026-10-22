using System.Collections;
using System.Collections.Generic;
using UnityEngine;


//그리퍼를 xDrive로 실제로 열고 닫으며, Grasp판정을 통과한 경우 양쪽 손가락이 닿는 순간 부품을 붙인다. 
// 판정 근거 : 마찰 원뿔, 손가락이 닫히는 방향과 손가락이 처음 닿을 부품면의 법선 사이 각도가 
// 마찰 원뿔 반각이내여야 미끄러지지않고 잡힌다

// 판정 시점 : 닫기 시작하기 직전 (부품이 손가락에 밀리거나 돌아가기전 상태로 판정)
// 판정 방법 : 손가락 패드 단직에 해당하는 3*3 광선 격자를 양쪽에서 쏴서 
// 각 쪽에서 먼저 맞는 면의 각도를 잰다. 

// 그리퍼 close -> 닫기 전 판정 -> 손가락을 조금씩 닫음
// -> 양쪽 손가락에 같은 부품이 닿았다면 (판정 통과 = 그 자리에서 멈추고 붙인다/ 판정 실패 = 살짝 별려서 미끄러지게함)

//그리퍼가 open -> 부품을 떼고 (Detach)손가락을 연다 -> 잠시뒤 로봇-부품 충돌을 다시 켬 

public class GripperController : MonoBehaviour
{
    [Header("손가락 AtriculationBody")]
    public ArticulationBody rightFinger;
    public ArticulationBody leftFinger;

    //각 손가락에 붙은 부품을 사용하려면 어떤 변수를 선언해야할까요?
    //변수 두개 만들기
    //각 손가락에 붙은 FingerContact "지금 어떤 부품에 닿아있는지" 알려줌 
    public FingerContact rightContact;
    public FingerContact leftContact;

    [Header("열림/닫힘 값")]
    //각 손가락 xDrive의 Target에 넣는 값
    //두 손가락이 마주 보고있으므로 부호가 반대
    //오른쪽 -0.005 열림, +0.005 닫힘
    //변수 4개 왼쪽 오른쪽 open,close 변수 만들고 초기값넣기 
    public float rightOpen = -0.005f;
    public float rightClose = 0.005f;
    public float leftOpen = 0.005f;
    public float leftClose = -0.005f;

    public float moveTime = 0.5f;//열림 ->닫힘까지 걸리는 시간 (초)
    public float extraCloseTime = 0.5f; //목표 닫힘에 도달한 뒤에도 손가락이 늦게 닿는것을 대비하여 기다리는 시간 

    //부품을 붙일 위치 
    //변수 
    [Header("부품을 붙일 위치 (손가락 사이 정중앙)")]
    public Transform attachPoint;

    //Grasp 파지 판정
    [Header("파지 판정")]
    public bool useGraspCheck = true;   //false면 양쪽이 닿기만하면 무조건 잡힘 
    public float frictionConfficient = 0.36f;  //약 19,8도 면이 이각보다 기얼어져있다면 미끄러진다 

    public float padHalfHeight = 0.006f;  // 패드 = 손가락 안쪽에 부품에 닿는 면
    public float padHalfWidth = 0.006f;   // 손가락 패드 절반 폭
    public float slipBackoff = 0.001f;   // 판정 실패시 손가락을 살짝 벌려 미끄러진 상태로 만드는 거리 

    public float restoreCollisionDelay = 0.3f; //놓자마자 충돌을 켜면 손가락과 겹친 부품이 튕겨나감. 부품이 빠질시간 추가 

    [Header("테스트")]
    public bool showTestButtons = true;  // 화면에 버튼 표시 여부

    public Rigidbody HeldPart { get; private set; }    //지금 잡고있는 부품 
    public bool IsBusy { get; private set; }  // 열고 닫는 중이면 true, 동작중 중복 실행을 막음

    //판정결과를 나중에 ML보상에 사용 
    public bool LastGraspOk { get; private set; }  // 잡기 성공 여부

    public float LastAngleRight { get; private set; }  // 오른쪽 손가락이 닿을 면의 기울기

    public float LastAngleLeft { get; private set; }

    public string LastGraspInfo { get; private set; } = "-";  // 판정 결과 사람이 읽을 문장으로 저장 

    //허용각도(도) = arctan(마찰계수)를 라디안 -> 도로 변환하기
    public float MaxAngle => Mathf.Atan(frictionConfficient) * Mathf.Rad2Deg;

    private Collider[] robotColliders; //로봇 존체의 콜라이더 목록. 샵은 부품과 충돌을 끌때 사용 

    readonly List<Collider> ignoredPartColliders = new List<Collider>(); //충돌을 꺼둔 부품 콜라이더 목록(다시 켜기 위해 기억)

    //디버그 표시용(onDrawGizoms)
    //dbgHitA, dbgHitB 양쪽 광선이 부품면에 처음 맞은 점 (월드 표시) 
    //dbgNormA, dbgNormB 그 점에서 부품 면의 법선(면의 수직, 길이1)
    //dbgVaild 그릴 값이 있는지. 판정 시작할때 false, 그리퍼 양쪽이 같은 부품에 맞으면 true
    private Vector3 dbgHitA, dbgHitB, dbgNormA, dbgNormB;
    private bool dbgVaild;

    private void Start()
    {
        //transform.root는 최상위 부모 (niryㅐ_one)을 말함. true 전달하여 꺼진 오브젝트 전체 콜라이더를 가져옴
        robotColliders = transform.root.GetComponentsInChildren<Collider>(true);

        //시작할때는 손가락이 열려있는 상태로 시작
        //TODO SetTargets
        SetTargets(rightOpen, leftOpen);
    }

    public IEnumerator Close()
    {
        if (IsBusy) yield break; // 이미 동작중이면 바로 끝냄

        IsBusy = true; // 동작 중 표시를 켬 

        LastGraspOk = false;  //지난번의 결과를 지우기

        //1. 닫기전에 판정(부품이 건드려지지 않은 상태)
        //닿은뒤에 판정하면 부품이 이미 밀리거나 돌아가 있어 정확하지 않음

        //잡을 수 있는 것인지 아닌지. 
        //TODO predictGrasp
        bool predictedOk = predictGrasp(out Rigidbody predictedPart);

        string pred = LastGraspInfo;

        //기록용 닫기전 부품 위치,회전,각 손가락이 처음 닿는 시간
        Vector3 partPos0 = predictedPart != null ? predictedPart.position : Vector3.zero;
        Quaternion partRot0 = predictedPart != null ? predictedPart.rotation : Quaternion.identity;

        float elapsed = 0f;  //몇초만에 부품이닿았는데
        float rightFirst = -1f;  //-1이 들어가있으면 한번도 닿지 않음
        float leftFirst = -1f;

        //손가락 닫기
        float startR = rightFinger.jointPosition[0];        // 관절이 실제로 지금 있는 위치. 손가락
        float startL = leftFinger.jointPosition[0];

        float t = 0f;    //진행률 0에서 1까지 가는 비율

        bool touched = false; // 양쪽이 같은 부품에 닿은적 있는지(원인구분용)
        float extra = 0f; //구간 대기시간 

        while (t<1f || extra < extraCloseTime)
        {
            if (t <1f)
            {
                //물리 스텝마다 그리퍼를 조금씩 닫기 . move time동안 닫히도록 
                t += Time.fixedDeltaTime / moveTime;
                SetTargets(Mathf.Lerp(startR, rightClose, t), Mathf.Lerp(startL, leftClose, t));
            }
            else
            {
                //목표는 이미 닫힘. 실제 손가락은 목표를 늦게 따라오므로 조금 더 기다림
                extra += Time.fixedDeltaTime;
            }

            elapsed += Time.fixedDeltaTime; //기록용 각 손가락이 처음 닫은 시각


            //rightFirst<0f 음수라는 뜻은 기록한적이없음 ,  rightContact.TouchingPart != null 지금 오른쪽 손가락이 부푸멩 닿아있음
            //양쪽 시간이 비슷하면 부품이 가운데, 사이가 크면 한쪽으로 치우쳐서 밀린것 -> 실패 원인
            if (rightFirst<0f && rightContact.TouchingPart != null)
            {
                rightFirst = elapsed;
            }

            if (leftFirst<0f && leftContact.TouchingPart != null)
            {
                leftFirst = elapsed;
            }

            //오른쪽 왼쪽이 같은 부품에 동시에 닿아있음 
            Rigidbody r = rightContact.TouchingPart;
            if (r != null && r ==leftContact.TouchingPart)
            {
                touched = true;
                float posR = rightFinger.jointPosition[0];
                float posL = leftFinger.jointPosition[0];

                //통과조건
                //판정을 무조건 통과해주거나 (!useGraspCheck) , 닫기전 판정이 성공이고 판정한 그 부품에 닿았을때 
                bool ok = !useGraspCheck || (predictedOk && r == predictedPart);

                if (ok)
                {
                    //판정이 통과했을때는 ->  Target 목표를 현재위치로 고정
                    SetTargets(posR, posL);  //현재 위치에서 정지
                    Attach(r);                   //부품을 Attach
                                                 //TODO Attach함수 만들기 
                    LastGraspOk = true;
                }
                else
                {
                    //통과못했을때는
                    //그리퍼 열기, 부품을 떨어지게 
                    //두손가락이 여는 방향은 부호가 반대(오른쪽은 - , 왼쪽은++
                    //Mathf.Sign이 하는 일은 숫자의 부호만 남김. 양수와 0 -> +1, 음수인 -1
                    //Sign으로부터 방향을 받고 slipBackOff(1mm)을 곱해서 거리를 구함 
                    SetTargets(posR + Mathf.Sign(rightOpen - rightClose) * slipBackoff,
                        posL + Mathf.Sign(leftOpen - leftClose) * slipBackoff);
                }
                break;
            }
            yield return new WaitForFixedUpdate();

        }

        //yield return new WaitForFixedUpdate();

        //결과정리 닫기전 판정, 실패 원인 2가지(닿기는했는데 각도 초과된경우, 양쪽 닫지않은경우
        string result;
        if (LastGraspOk)
        {
            result = "성공";
        }
        else if (touched)
        {
            result ="실패 : 미끄러짐(판정 각도가 초과)";
        }
        else
        {
            result ="실패 : 양쪽 손가락이 동시에 닿지 않음";
        }
        LastGraspInfo = $"{result}\n 닫기전 판정:{pred}";

        //콘솔 기록 
        string rightTime = "안닿음";
        if (rightFirst >= 0f)
        {
            rightTime = $"{rightFirst:F2}초";
        }

        string leftTime = "안닿음";
        if (leftFirst >= 0f)
        {
            leftTime = $"{leftTime:F2}초";
        }

        //닫는동안 부품이 밀린거리, 돌아간 각도 -> 크면 파지위치가 나빳다는 뜻
        float moveMm = 0f;
        float turnDeg = 0f;

        if (predictedPart !=null)
        {
            moveMm = Vector3.Distance(predictedPart.position, partPos0)*100; // m->mm
            turnDeg = Quaternion.Angle(predictedPart.rotation, partRot0);
        }

        //손가락 실제 위치와 목표(차이가 크면 부품에 막혀있다는 뜻
        float rightPos = rightFinger.jointPosition[0];
        float leftPos = rightFinger.jointPosition[0];
        float rightTarget = rightFinger.xDrive.target;
        float leftTarget = leftFinger.xDrive.target;

        Debug.Log($"[Grasp] {result} | 닫기전 판정: {pred}" +
            $"처음닫은 시간 R {rightTime}, L{leftTime} | +" +
            $"손가락 위치 {rightPos:F4} / {leftPos:F4} (목표 {rightTarget:F4} / {leftTarget:F4} |"+
            $"부품 이동{moveMm:F1}mm, 회전{turnDeg:F1}도");

        IsBusy = false;
    }

    public IEnumerator Open()
    {
        if (IsBusy) yield break;

        IsBusy = true;

        //먼저 부품에 손에서 떼고, 물리 다시 적용
        Detach();

        //손가락 열기 
        float startR = rightFinger.jointPosition[0];
        float startL = leftFinger.jointPosition[0];

        float t = 0f;

        while (t<1f)
        {
            t += Time.fixedDeltaTime / moveTime;  //moveTime동안 열림 위치까지 

            //왼쪽 손가락 열기, 오른쪽 손가락 열기 Lerp
            //close 함수 예시 Mathf.Lerp(startR, rightClose, t)
            SetTargets(Mathf.Lerp(startR, rightOpen, t), Mathf.Lerp(startL, leftOpen, t));
            yield return new WaitForFixedUpdate();
        }

        //부품이 손가락에서 완전히 빠진 뒤에는 로봇,부품 충돌을 다시 켠다 
        yield return new WaitForSeconds(restoreCollisionDelay);

        SetIgnoreCollision(false);

        IsBusy = false;
    }

    //부품 붙이기
    private void Attach(Rigidbody part)
    {
        HeldPart = part;
        part.isKinematic = true;   //물리 계산에서 빼서 중력, 충돌에 의해 부품이 흔들리지 않게됨
        part.transform.SetParent(attachPoint, true); //attachpoint의 자식으로 설정하고 true 는 지금 월드 위치와 회전은 그대로 유지 

        //잡은 부품이 손가락과 계속 부딪히면 떨리는 문제가 발생할수있다
        ignoredPartColliders.Clear();     //1. 목록을 비움
        ignoredPartColliders.AddRange(part.GetComponentsInChildren<Collider>()); //2. 부품의 콜라이더를 전부 목록에 넣는다

        //TODO SetIgnoreCollision
        SetIgnoreCollision(true);
    }

    private void Detach()
    {
        //HeldPart = null;
        if (HeldPart == null) return;

        //부품 부모를 해제, 물리를 다시 적용 

        HeldPart.transform.SetParent(null, true);
        HeldPart.isKinematic = false;
        HeldPart = null;

        //fingerContact에 남아있는 접촉 기록 지움 
        rightContact.Clear();
        leftContact.Clear();
    }

    private void SetIgnoreCollision(bool ignore)
    {
        foreach (Collider partCollider in ignoredPartColliders)
        {
            if (partCollider == null) continue;

            foreach (Collider robotCollider in robotColliders)
            {
                //로봇 콜라이더가 삭제됐거나 같은 콜라이더면 건너 뜀
                if (robotCollider != null && robotCollider != partCollider)
                {
                    Physics.IgnoreCollision(partCollider, robotCollider, ignore);
                }
            }
        }

        //다시 켰으면 목록 비움(다음에 잡을 부품을 새로 넣기 위해 
        if (!ignore)
        {
            ignoredPartColliders.Clear();
        }
    }

    //그랩 판정 (닫기 전)
    //반환 (잡을 수 있으면 true, 아니면 false 반환) out part 변수를 만들어서 손가락 사이에있는 부품
    // 잡힐지 안잡힐지 (bool) 함수의 return으로 돌려줌
    //어떤 부품을 판정했는지 (Rigidbody) out으로 돌려줌
    //out은 함수가 return 말고 값을 하나 더 돌려주는 통로 

    //predictGrasp는 손가락 패드에서 레이저 9개를 쏜다 
    // 그리퍼 기준 "앞, 위, 옆, 시작점"이 필요함
    // 그리퍼가 계속 돌아갈 예정이니 그리퍼 기준의 값이 필요함 

    private bool predictGrasp(out Rigidbody part)
    {
        part = null;
        dbgVaild = false;  //그릴것이 없으므로 리셋

        //그리퍼는 손목이 돌고 팔이 기울어짐 계속 비틀어진다 
        //그래서 월드 x,y,z를 그대로 쓰기는 어려우므로 그리퍼 기준 방향 3가지를 만듦
        //axis = 손가락이 열고닫히는 방향
        //up = 손가락 패드면 세로 방향
        //side = 손가락 패드면의 가로방향
        //center = 손가락 사이의 중심

        //그 방향을 회전 시킨 값 = 회전(Quaternion)* 방향
        //Vector3.right (1,0,0) x축 방향
        //.trtransform.rotation을 곱해서 손가락 기준 방향을 월드방향으로 바꿈
        // normalized을 통해 길이를 1로 맞추고 방향만 남김

        //axis는 앞 손가락이 닫히는 방향 = 레이저를 쏘는 방향
        //손가락 관절은 자기x축으로 미끄러진다 =  그 x축이 world에서 어느쪽인지 구한다
        Vector3 axis = (rightFinger.transform.rotation * (rightFinger.anchorRotation * Vector3.right)).normalized;

        //손가락 패드면 세로방향 월드 위쪽을 axis에 수직이 되게 맞춰야함
        //일반 Vector3.up을 쓰면 안되는 이유는 로봇 팔 자세에 따라 그리퍼가 조금 기울어지면 손가락 방향도 위아래로 기울어짐
        //그래서 ProjectOnPlane로 보정해서 항상 손가락 방향과 수직인 위쪽을 돌려주도록함

        //up(위) 패드의 세로방향 
        //그리퍼가 기울어져있을때는 앞과 직각이안됨. 직각이 되게끔 고쳐줘야함
        Vector3 up = Vector3.ProjectOnPlane(Vector3.up, axis).normalized;

        //손가락 패드의 가로 방향. 외적 = axis와 up 둘다에 수직인 방향
        //side(옆) 패드의 가로방향
        // cross= 두방향 모두에 직각이 방향. 앞가 위를 알면 옆이 나옴 외적 계산
        Vector3 side = Vector3.Cross(axis, up).normalized;

        //손가락 사이의 중심
        //레이저 격자의 한가운데 
        Vector3 center = attachPoint.position;

        //TODO FirstContact 함수를 만들기

        //한쪽이라도 모 맞췄거나 양쪽이 서로다른 부품을 맞췄으면 실패

        //Right, left에서 광선 발사 
        //a(right) b(left)

        //오른쪽 손가락에서 중심을 향해 레이저 9개를 쏜다
        bool okA = FirstContact(center, axis, up, side, +1, out RaycastHit a, out Rigidbody partA);

        //왼쪽 손가락에서 중심을 향해 레이저 9개를 쏜다
        bool okB = FirstContact(center, axis, up, side, -1, out RaycastHit b, out Rigidbody partB);

        //한쪽이라도 못맞췄거나 , 양쪽이 서로 다른 부품을 맞췄다면 -> 실패 

        if (!okA || !okB || partA != partB)
        {
            LastGraspInfo = "실패 : 손가락 사이에 부품없음";

            return false;
        }

        part = partA;

        //a,b는 광선이 맞은 정보
        //normal = 법선 (그 면에서 바깥으로 수직으로 솟은 뱡향)
        //정면으로 잡았을때는 0도, 비스듬하게 잡았을때 각도가 커진다

        LastAngleRight = Vector3.Angle(a.normal, axis);
        LastAngleLeft = Vector3.Angle(b.normal, -axis);

        dbgHitA = a.point;
        dbgHitB = b.point;

        dbgNormA = a.normal;
        dbgNormB = b.normal;
        dbgVaild = true;


        // 양쪽 모두 허용 각도 이내여야 성공
        bool ok = LastAngleRight <= MaxAngle && LastAngleLeft <= MaxAngle;
        LastGraspInfo = $"{(ok ? "성공" : "실패 : 미끄러짐")} ({LastAngleRight:F1}도, {LastAngleLeft:F1}도 / 허용 {MaxAngle:F1}도)";
        return ok;
    }

    //best = 가장 가까운 맞은 지점의 정보
    //bestPart = 그면이 속한 부품
    //boolen 부품을 맞췄다면 true
    private bool FirstContact(Vector3 center, Vector3 axis, Vector3 up, Vector3 side, float sideSign, out RaycastHit best, out Rigidbody bestPart)
    {
        best = default;    //시작 빈값
        bestPart = null;   // 아직 맞힌 부품이 없음

        float bestDist = float.MaxValue; // 그중 가장 큰수 초깃값

        //광선 방향을 결정. 손가락이 닫혀오는 방향에서 attach point광선을 쏴야함
        Vector3 dir = -sideSign * axis;

        for (int iu = -1; iu <=1; iu++)   // iu는 -1는(위) 0은 (가운데) 1은 (아래)
        {
            for (int iv = -1; iv <=1; iv++)
            {
                // Vector3 origin = center + up * (iu * padHalfHeight) + side * (iv * padHalfHeight) + sideSign * axis * 0.1f;

                //레이저 출발점 정하기. 중심에서 시작해서 한단계씩 옮긴다
                Vector3 origin = center;                    // 1. 손가락 사이 중심에서 출발
                origin +=  up * (iu * padHalfHeight);       // 2. 위 아래로 이동
                origin += side * (iv * padHalfHeight);      // 3. 좌우로 이동 
                origin += sideSign * axis * 0.1f;           // 4. 손가락 바깥으로 10cm 물러나서 안쪽으로 쏜다. 

                //Raycast 처음 하나만 돌려줌. RaycastAll 광선이 지나는 모든 콜라이더를 돌려줌
                //손가락이 다른 물체에 먼저 맞았더라하더라도 그 뒤의 부품까지 찾기위해서 all을 쓴다 

                foreach (var h in Physics.RaycastAll(origin, dir, 0.2f, ~0, QueryTriggerInteraction.Ignore))  // => 광선쏘는 코드 
                {
                    //맞은 콜라이더가 속한 Rigidbody(부픔) 
                    Rigidbody rb = h.collider.attachedRigidbody;

                    if (rb == null || !rb.CompareTag("Part") || rb == HeldPart) { continue; }

                    //지금까지의 기록보다 더 가까우면 새로 기록한다
                    if (h.distance < bestDist)
                    {
                        bestDist = h.distance;   //가장 가까운 거리 갱신
                        best = h;       // 그 지정 정보 갱신
                        bestPart = rb;
                    }
                }
            }
        }
        return bestPart != null;  //9게 광선중 하나라도 부품에 맞았다면 true
    }

    private void SetTargets(float right, float left)
    {
        //x Drive = struct(값 복사) 꺼내서 고치고 다시 넣어줘야 적용됨

        var d = rightFinger.xDrive;   //1. 설정 묶음을 복사
        d.target = right;             //2. 복사본의 Target만 고침(다른 값은 그대로)
        rightFinger.xDrive = d;       //3. 고친 복사본을 관절에 다시 넣음(이때 실제로 적용됨)

        d=leftFinger.xDrive;
        d.target = left;
        leftFinger.xDrive = d;
    }

    //디버그 표시하는 함수 
    //OnDrawGizmos 함수로 씬뷰에 라인을 표시함 
    //흰선 = 두 접촉점은 이은선, 노란색 선 = 손가락이 처음 닿을 면의 법선 
    private void OnDrawGizmos()
    {
        if(!dbgVaild) return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(dbgHitA, dbgHitA + dbgNormA * 0.03f); //노란 법선의 길이는 3cm
        Gizmos.DrawLine(dbgHitB, dbgHitB + dbgNormB * 0.03f);

        Gizmos.color = Color.white;
        Gizmos.DrawLine(dbgHitA, dbgHitB);
        Gizmos.DrawSphere(dbgHitA, 0.0015f);  //반지름 1.5mm크기 
        Gizmos.DrawSphere(dbgHitB, 0.0015f);  //반지름 1.5mm크기 
    }
    static string Name(Rigidbody rb) => rb !=null ? rb.name : "-"; //존재할때만 이름으로 표시

    private void OnGUI()
    {
        if(!showTestButtons) return;

        GUILayout.BeginArea(new Rect(20,20, 200,200), GUI.skin.box);

        if (GUILayout.Button("Open"))
        {
            StartCoroutine(Open());
        }

        if (GUILayout.Button("Close"))
        {
            StartCoroutine(Close());
        }

        GUILayout.Label($"Right / Left 위치{rightFinger.jointPosition[0]:F4} / {leftFinger.jointPosition[0]:F4}");
        GUILayout.Label($"닿음: {Name(rightContact.TouchingPart)} /{Name(leftContact.TouchingPart)} ");
        GUILayout.Label($"잡은 부품: {Name(HeldPart)}");
        GUILayout.Label($"판정: {LastGraspInfo}");

        GUILayout.EndArea();
    }
}

